CREATE TABLE effect_checkpoints (
    organization_id uuid NOT NULL,
    attempt_id uuid NOT NULL,
    effect_key text NOT NULL
        CHECK (char_length(effect_key) BETWEEN 1 AND 256 AND btrim(effect_key) <> ''),
    fence bigint NOT NULL CHECK (fence >= 0),
    authority_owner text NOT NULL CHECK (btrim(authority_owner) <> ''),
    restore_epoch bigint NOT NULL CHECK (restore_epoch >= 0),
    payload_digest bytea NOT NULL CHECK (octet_length(payload_digest) = 32),
    state text NOT NULL CHECK (state IN ('prepared', 'applied', 'confirmed', 'uncertain')),
    uncertain_from text CHECK (uncertain_from IN ('prepared', 'applied')),
    prepared_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    applied_at timestamptz,
    confirmed_at timestamptz,
    uncertain_at timestamptz,
    PRIMARY KEY (organization_id, attempt_id, effect_key),
    FOREIGN KEY (attempt_id, organization_id)
        REFERENCES attempts (id, organization_id),
    CHECK (
        (state = 'prepared'
            AND uncertain_from IS NULL
            AND applied_at IS NULL
            AND confirmed_at IS NULL
            AND uncertain_at IS NULL)
        OR
        (state = 'applied'
            AND uncertain_from IS NULL
            AND applied_at IS NOT NULL
            AND confirmed_at IS NULL
            AND uncertain_at IS NULL)
        OR
        (state = 'confirmed'
            AND uncertain_from IS NULL
            AND applied_at IS NOT NULL
            AND confirmed_at IS NOT NULL
            AND uncertain_at IS NULL)
        OR
        (state = 'uncertain'
            AND uncertain_from = 'prepared'
            AND applied_at IS NULL
            AND confirmed_at IS NULL
            AND uncertain_at IS NOT NULL)
        OR
        (state = 'uncertain'
            AND uncertain_from = 'applied'
            AND applied_at IS NOT NULL
            AND confirmed_at IS NULL
            AND uncertain_at IS NOT NULL)
    )
);

CREATE INDEX effect_checkpoints_uncertain
    ON effect_checkpoints (organization_id, prepared_at, attempt_id, effect_key)
    WHERE state = 'uncertain';

CREATE FUNCTION fogell_guard_effect_checkpoint()
RETURNS trigger
LANGUAGE plpgsql
AS $$
DECLARE
    has_authority boolean;
    observed_at timestamptz := clock_timestamp();
BEGIN
    IF TG_OP = 'DELETE' THEN
        RAISE EXCEPTION 'effect checkpoints cannot be deleted';
    ELSIF TG_OP = 'INSERT' THEN
        IF NEW.state <> 'prepared' THEN
            RAISE EXCEPTION 'effect checkpoint must begin prepared';
        END IF;
    ELSE
        IF ROW(
            NEW.organization_id,
            NEW.attempt_id,
            NEW.effect_key,
            NEW.fence,
            NEW.authority_owner,
            NEW.restore_epoch,
            NEW.payload_digest,
            NEW.prepared_at
        ) IS DISTINCT FROM ROW(
            OLD.organization_id,
            OLD.attempt_id,
            OLD.effect_key,
            OLD.fence,
            OLD.authority_owner,
            OLD.restore_epoch,
            OLD.payload_digest,
            OLD.prepared_at
        ) THEN
            RAISE EXCEPTION 'effect checkpoint identity, authority, digest, and preparation time are immutable';
        END IF;

        IF NOT (
            (OLD.state = 'prepared' AND NEW.state IN ('applied', 'uncertain'))
            OR (OLD.state = 'applied' AND NEW.state IN ('confirmed', 'uncertain'))
        ) THEN
            RAISE EXCEPTION 'illegal effect checkpoint transition % -> %', OLD.state, NEW.state;
        END IF;

        IF OLD.applied_at IS NOT NULL
           AND NEW.applied_at IS DISTINCT FROM OLD.applied_at THEN
            RAISE EXCEPTION 'effect application time is immutable once recorded';
        END IF;
    END IF;

    IF NEW.prepared_at > observed_at
       OR NEW.applied_at > observed_at
       OR NEW.confirmed_at > observed_at
       OR NEW.uncertain_at > observed_at THEN
        RAISE EXCEPTION 'effect checkpoint timestamps cannot be in the future';
    END IF;

    IF NEW.applied_at IS NOT NULL AND NEW.applied_at < NEW.prepared_at THEN
        RAISE EXCEPTION 'effect application cannot precede preparation';
    END IF;

    IF NEW.confirmed_at IS NOT NULL AND NEW.confirmed_at < NEW.applied_at THEN
        RAISE EXCEPTION 'effect confirmation cannot precede application';
    END IF;

    IF NEW.uncertain_at IS NOT NULL
       AND (NEW.uncertain_at < NEW.prepared_at
            OR (NEW.applied_at IS NOT NULL AND NEW.uncertain_at < NEW.applied_at)) THEN
        RAISE EXCEPTION 'effect uncertainty cannot precede its known history';
    END IF;

    PERFORM 1
    FROM attempts a
    WHERE a.organization_id = NEW.organization_id
      AND a.id = NEW.attempt_id
      AND a.fence = NEW.fence
      AND a.lease_owner = NEW.authority_owner
      AND a.lease_expires_at > clock_timestamp()
      AND a.state IN ('offered', 'accepted', 'running', 'finalizing', 'cancelling')
      AND a.restore_epoch = NEW.restore_epoch
      AND a.restore_epoch = (
          SELECT restore_epoch
          FROM controller_metadata
          WHERE singleton
      )
    FOR UPDATE;
    has_authority := FOUND;

    IF NEW.state = 'uncertain' THEN
        IF has_authority THEN
            RAISE EXCEPTION 'a live effect checkpoint cannot become uncertain';
        END IF;
    ELSIF NOT has_authority THEN
        RAISE EXCEPTION 'effect checkpoint authority is stale or invalid';
    END IF;

    RETURN NEW;
END;
$$;

CREATE TRIGGER effect_checkpoints_guard
BEFORE INSERT OR UPDATE OR DELETE ON effect_checkpoints
FOR EACH ROW
EXECUTE FUNCTION fogell_guard_effect_checkpoint();
