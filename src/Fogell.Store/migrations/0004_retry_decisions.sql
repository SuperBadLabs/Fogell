-- FG-027b. One durable retry decision per immutable parent attempt.
--
-- The row captures every parent field consumed by the Domain retry law.  A
-- child outcome additionally points at the canonical queued child created in
-- the same transaction; an exhausted outcome instead carries one exact,
-- stable dead-letter reason.

CREATE TABLE retry_decisions (
    organization_id uuid NOT NULL,
    parent_attempt_id uuid NOT NULL,
    parent_node_id uuid NOT NULL,
    parent_ordinal integer NOT NULL CHECK (parent_ordinal >= 0 AND parent_ordinal < 2147483647),
    parent_retry_of uuid,
    parent_restore_epoch bigint NOT NULL CHECK (parent_restore_epoch >= 0),
    attempt_limit integer NOT NULL CHECK (attempt_limit > 0),
    outcome text NOT NULL CHECK (outcome IN ('child_created', 'budget_exhausted')),
    child_attempt_id uuid,
    dead_letter_reason text,
    decided_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    PRIMARY KEY (organization_id, parent_attempt_id),
    FOREIGN KEY (parent_attempt_id, organization_id)
        REFERENCES attempts (id, organization_id),
    FOREIGN KEY (parent_retry_of, organization_id)
        REFERENCES attempts (id, organization_id),
    FOREIGN KEY (child_attempt_id, organization_id)
        REFERENCES attempts (id, organization_id),
    UNIQUE (organization_id, child_attempt_id),
    CHECK (
        (outcome = 'child_created'
            AND child_attempt_id IS NOT NULL
            AND dead_letter_reason IS NULL)
        OR
        (outcome = 'budget_exhausted'
            AND child_attempt_id IS NULL
            AND dead_letter_reason = 'attempt budget exhausted')
    )
);

CREATE INDEX retry_decisions_dead_letters
    ON retry_decisions (organization_id, decided_at, parent_attempt_id)
    WHERE outcome = 'budget_exhausted';

-- Attempts are mutable state machines, but their identity and ancestry are
-- not mutable.  Retry replay depends on those fields remaining the snapshot
-- recorded by the decision.
CREATE FUNCTION fogell_guard_attempt_identity()
RETURNS trigger
LANGUAGE plpgsql
AS $$
BEGIN
    IF ROW(
        NEW.id,
        NEW.organization_id,
        NEW.node_id,
        NEW.ordinal,
        NEW.retry_of,
        NEW.restore_epoch,
        NEW.created_at
    ) IS DISTINCT FROM ROW(
        OLD.id,
        OLD.organization_id,
        OLD.node_id,
        OLD.ordinal,
        OLD.retry_of,
        OLD.restore_epoch,
        OLD.created_at
    ) THEN
        RAISE EXCEPTION 'attempt identity, lineage, restore epoch, and creation time are immutable';
    END IF;

    RETURN NEW;
END;
$$;

CREATE TRIGGER attempts_identity_guard
BEFORE UPDATE ON attempts
FOR EACH ROW
EXECUTE FUNCTION fogell_guard_attempt_identity();

CREATE FUNCTION fogell_guard_retry_decision()
RETURNS trigger
LANGUAGE plpgsql
AS $$
DECLARE
    current_epoch bigint;
    parent_row attempts%ROWTYPE;
    child_row attempts%ROWTYPE;
BEGIN
    IF TG_OP = 'UPDATE' THEN
        RAISE EXCEPTION 'retry decisions are immutable';
    ELSIF TG_OP = 'DELETE' THEN
        RAISE EXCEPTION 'retry decisions cannot be deleted';
    END IF;

    -- Share-lock the restore epoch before reading either attempt.  Restore
    -- takes the conflicting row lock, so a fresh decision and epoch bump have
    -- a deterministic serialization point rather than a check/use window.
    SELECT restore_epoch INTO STRICT current_epoch
    FROM controller_metadata
    WHERE singleton
    FOR SHARE;

    SELECT * INTO STRICT parent_row
    FROM attempts
    WHERE organization_id = NEW.organization_id
      AND id = NEW.parent_attempt_id
    FOR UPDATE;

    IF parent_row.state <> 'terminal'
       OR parent_row.result IS NULL
       OR parent_row.result NOT IN ('not_built', 'success', 'unstable', 'failure', 'aborted') THEN
        RAISE EXCEPTION 'retry parent must be terminal with a valid build result';
    END IF;

    IF ROW(
        NEW.parent_node_id,
        NEW.parent_ordinal,
        NEW.parent_retry_of,
        NEW.parent_restore_epoch
    ) IS DISTINCT FROM ROW(
        parent_row.node_id,
        parent_row.ordinal,
        parent_row.retry_of,
        parent_row.restore_epoch
    ) THEN
        RAISE EXCEPTION 'retry parent snapshot does not match durable parent';
    END IF;

    IF NEW.parent_restore_epoch <> current_epoch THEN
        RAISE EXCEPTION 'fresh retry decision cannot cross a restore epoch';
    END IF;

    IF NEW.outcome = 'child_created' THEN
        IF NEW.parent_ordinal + 1 >= NEW.attempt_limit THEN
            RAISE EXCEPTION 'retry child is at or beyond the attempt limit';
        END IF;

        SELECT * INTO STRICT child_row
        FROM attempts
        WHERE organization_id = NEW.organization_id
          AND id = NEW.child_attempt_id
        FOR UPDATE;

        IF ROW(
            child_row.node_id,
            child_row.ordinal,
            child_row.retry_of,
            child_row.state,
            child_row.fence,
            child_row.restore_epoch,
            child_row.lease_owner,
            child_row.lease_expires_at,
            child_row.result
        ) IS DISTINCT FROM ROW(
            NEW.parent_node_id,
            NEW.parent_ordinal + 1,
            NEW.parent_attempt_id,
            'queued'::text,
            0::bigint,
            NEW.parent_restore_epoch,
            NULL::text,
            NULL::timestamptz,
            NULL::text
        ) THEN
            RAISE EXCEPTION 'retry child is not the canonical queued creation snapshot';
        END IF;
    ELSIF NEW.parent_ordinal + 1 < NEW.attempt_limit THEN
        RAISE EXCEPTION 'retry budget was exhausted below the attempt limit';
    END IF;

    IF NEW.decided_at > clock_timestamp() THEN
        RAISE EXCEPTION 'retry decision time cannot be in the future';
    END IF;

    RETURN NEW;
END;
$$;

CREATE TRIGGER retry_decisions_guard
BEFORE INSERT OR UPDATE OR DELETE ON retry_decisions
FOR EACH ROW
EXECUTE FUNCTION fogell_guard_retry_decision();

-- Event and outbox publication are consequences of inserting the immutable
-- decision.  Keeping them in the trigger means even an administrative SQL
-- caller cannot commit one without the other two records.
CREATE FUNCTION fogell_publish_retry_decision()
RETURNS trigger
LANGUAGE plpgsql
AS $$
DECLARE
    parent_build_id uuid;
    decision_payload jsonb;
BEGIN
    SELECT build_id INTO STRICT parent_build_id
    FROM nodes
    WHERE organization_id = NEW.organization_id
      AND id = NEW.parent_node_id;

    decision_payload := jsonb_build_object(
        'parentAttempt', NEW.parent_attempt_id,
        'attemptLimit', NEW.attempt_limit,
        'outcome', NEW.outcome,
        'childAttempt', NEW.child_attempt_id,
        'deadLetterReason', NEW.dead_letter_reason,
        'restoreEpoch', NEW.parent_restore_epoch
    );

    INSERT INTO events (organization_id, build_id, attempt_id, kind, payload)
    VALUES (
        NEW.organization_id,
        parent_build_id,
        NEW.parent_attempt_id,
        'retry.decided',
        decision_payload
    );

    INSERT INTO outbox (organization_id, topic, body)
    VALUES (NEW.organization_id, 'retry.decided', decision_payload);

    RETURN NEW;
END;
$$;

CREATE TRIGGER retry_decisions_publish
AFTER INSERT ON retry_decisions
FOR EACH ROW
EXECUTE FUNCTION fogell_publish_retry_decision();
