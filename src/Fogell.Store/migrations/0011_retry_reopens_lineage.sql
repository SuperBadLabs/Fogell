-- FG-027b / FG-224. A fresh retry child is executable work, so its public
-- node/build aggregate must be reopened in the same transaction. Store performs
-- the transition explicitly for clear API semantics; this trigger independently
-- owns the invariant for direct SQL writers and future call paths.

-- Repair decisions committed before this invariant existed. Migration 0005
-- FORCEs tenant RLS, so a NOBYPASSRLS schema owner must temporarily lower the
-- boundary to enumerate every tenant. The migration runner wraps this file in
-- one transaction; any repair or publication failure rolls back these ALTERs,
-- and protection is restored before commit.
ALTER TABLE retry_decisions NO FORCE ROW LEVEL SECURITY;
ALTER TABLE retry_decisions DISABLE ROW LEVEL SECURITY;
ALTER TABLE attempts NO FORCE ROW LEVEL SECURITY;
ALTER TABLE attempts DISABLE ROW LEVEL SECURITY;
ALTER TABLE nodes NO FORCE ROW LEVEL SECURITY;
ALTER TABLE nodes DISABLE ROW LEVEL SECURITY;
ALTER TABLE builds NO FORCE ROW LEVEL SECURITY;
ALTER TABLE builds DISABLE ROW LEVEL SECURITY;
ALTER TABLE events NO FORCE ROW LEVEL SECURITY;
ALTER TABLE events DISABLE ROW LEVEL SECURITY;
ALTER TABLE outbox NO FORCE ROW LEVEL SECURITY;
ALTER TABLE outbox DISABLE ROW LEVEL SECURITY;

CREATE TEMPORARY TABLE fogell_retry_lineage_repairs AS
SELECT d.organization_id,
       d.parent_attempt_id,
       d.child_attempt_id,
       d.parent_node_id,
       n.build_id,
       b.cancellation_requested
  FROM retry_decisions d
  JOIN attempts p
    ON p.organization_id = d.organization_id
   AND p.id = d.parent_attempt_id
  JOIN attempts c
    ON c.organization_id = d.organization_id
   AND c.id = d.child_attempt_id
  JOIN nodes n
    ON n.organization_id = d.organization_id
   AND n.id = d.parent_node_id
  JOIN builds b
    ON b.organization_id = n.organization_id
   AND b.id = n.build_id
 WHERE d.outcome = 'child_created'
   AND c.state IN ('queued', 'offered')
   AND n.status IN (p.result, 'queued')
   AND b.status IN (p.result, 'queued')
   AND (n.status <> 'queued' OR b.status <> 'queued');

UPDATE nodes n
   SET status = 'queued'
  FROM fogell_retry_lineage_repairs r
 WHERE n.organization_id = r.organization_id
   AND n.id = r.parent_node_id
   AND n.status <> 'queued';

UPDATE builds b
   SET status = 'queued'
  FROM fogell_retry_lineage_repairs r
 WHERE b.organization_id = r.organization_id
   AND b.id = r.build_id
   AND b.status <> 'queued';

INSERT INTO events (organization_id, build_id, attempt_id, kind, payload)
SELECT r.organization_id,
       r.build_id,
       r.parent_attempt_id,
       'build.reopened',
       jsonb_build_object(
           'build', r.build_id,
           'parentAttempt', r.parent_attempt_id,
           'childAttempt', r.child_attempt_id,
           'buildStatus', 'queued',
           'cancellationRequested', r.cancellation_requested,
           'reason', 'migration_0011_retry_repair')
  FROM fogell_retry_lineage_repairs r;

INSERT INTO outbox (organization_id, topic, body)
SELECT r.organization_id,
       'build.reopened',
       jsonb_build_object(
           'build', r.build_id,
           'parentAttempt', r.parent_attempt_id,
           'childAttempt', r.child_attempt_id,
           'buildStatus', 'queued',
           'cancellationRequested', r.cancellation_requested,
           'reason', 'migration_0011_retry_repair')
  FROM fogell_retry_lineage_repairs r;

DROP TABLE fogell_retry_lineage_repairs;

ALTER TABLE retry_decisions ENABLE ROW LEVEL SECURITY;
ALTER TABLE retry_decisions FORCE ROW LEVEL SECURITY;
ALTER TABLE attempts ENABLE ROW LEVEL SECURITY;
ALTER TABLE attempts FORCE ROW LEVEL SECURITY;
ALTER TABLE nodes ENABLE ROW LEVEL SECURITY;
ALTER TABLE nodes FORCE ROW LEVEL SECURITY;
ALTER TABLE builds ENABLE ROW LEVEL SECURITY;
ALTER TABLE builds FORCE ROW LEVEL SECURITY;
ALTER TABLE events ENABLE ROW LEVEL SECURITY;
ALTER TABLE events FORCE ROW LEVEL SECURITY;
ALTER TABLE outbox ENABLE ROW LEVEL SECURITY;
ALTER TABLE outbox FORCE ROW LEVEL SECURITY;

CREATE FUNCTION fogell_reopen_retry_lineage()
RETURNS trigger
LANGUAGE plpgsql
SET search_path = pg_catalog, pg_temp
AS $$
DECLARE
    parent_result text;
    parent_build_id uuid;
    node_status text;
    build_status text;
BEGIN
    IF NEW.outcome <> 'child_created' THEN
        RETURN NEW;
    END IF;

    SELECT result
      INTO STRICT parent_result
      FROM public.attempts
     WHERE organization_id = NEW.organization_id
       AND id = NEW.parent_attempt_id
     FOR UPDATE;

    SELECT build_id, status
      INTO STRICT parent_build_id, node_status
      FROM public.nodes
     WHERE organization_id = NEW.organization_id
       AND id = NEW.parent_node_id
     FOR UPDATE;

    IF node_status = parent_result THEN
        UPDATE public.nodes
           SET status = 'queued'
         WHERE organization_id = NEW.organization_id
           AND id = NEW.parent_node_id;
    ELSIF node_status <> 'queued' THEN
        RAISE EXCEPTION
            'retry parent node status % is neither parent result % nor queued',
            node_status,
            parent_result
            USING ERRCODE = '23514';
    END IF;

    SELECT status
      INTO STRICT build_status
      FROM public.builds
     WHERE organization_id = NEW.organization_id
       AND id = parent_build_id
     FOR UPDATE;

    IF build_status = parent_result THEN
        -- Preserve cancellation_requested. A pre-existing cancellation is
        -- authority that BeginExecution must honor before launching the child.
        UPDATE public.builds
           SET status = 'queued'
         WHERE organization_id = NEW.organization_id
           AND id = parent_build_id;
    ELSIF build_status <> 'queued' THEN
        RAISE EXCEPTION
            'retry parent build status % is neither parent result % nor queued',
            build_status,
            parent_result
            USING ERRCODE = '23514';
    END IF;

    RETURN NEW;
END;
$$;

-- PostgreSQL fires same-kind triggers alphabetically. `guard` first proves the
-- immutable parent/child snapshot; `reopen_lineage` then changes only aggregate
-- visibility before the AFTER publication trigger can emit retry.decided.
CREATE TRIGGER retry_decisions_reopen_lineage
BEFORE INSERT ON retry_decisions
FOR EACH ROW
EXECUTE FUNCTION fogell_reopen_retry_lineage();

-- retry.decided is the exact-once append-only compensation for the earlier
-- build.terminal publication. Bind it to the build so an outbox consumer can
-- apply the queued transition without reconstructing parent lineage.
CREATE OR REPLACE FUNCTION fogell_publish_retry_decision()
RETURNS trigger
LANGUAGE plpgsql
SET search_path = pg_catalog, pg_temp
AS $$
DECLARE
    parent_build_id uuid;
    decision_payload jsonb;
BEGIN
    SELECT build_id INTO STRICT parent_build_id
    FROM public.nodes
    WHERE organization_id = NEW.organization_id
      AND id = NEW.parent_node_id;

    decision_payload := jsonb_build_object(
        'build', parent_build_id,
        'parentAttempt', NEW.parent_attempt_id,
        'attemptLimit', NEW.attempt_limit,
        'outcome', NEW.outcome,
        'childAttempt', NEW.child_attempt_id,
        'deadLetterReason', NEW.dead_letter_reason,
        'restoreEpoch', NEW.parent_restore_epoch
    );

    IF NEW.outcome = 'child_created' THEN
        decision_payload := decision_payload || jsonb_build_object('buildStatus', 'queued');
    END IF;

    INSERT INTO public.events (organization_id, build_id, attempt_id, kind, payload)
    VALUES (
        NEW.organization_id,
        parent_build_id,
        NEW.parent_attempt_id,
        'retry.decided',
        decision_payload
    );

    INSERT INTO public.outbox (organization_id, topic, body)
    VALUES (NEW.organization_id, 'retry.decided', decision_payload);

    RETURN NEW;
END;
$$;
