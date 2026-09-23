-- FG-268. Durable deletion intent; tombstones preserve build identity forever.
CREATE TABLE build_retention (
    organization_id uuid NOT NULL,
    build_id uuid NOT NULL,
    state text NOT NULL CHECK (state IN ('selected','deleting','expired','held')),
    restore_epoch bigint NOT NULL,
    source_digest bytea,
    admission_fingerprint bytea,
    manifest jsonb NOT NULL,
    selected_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    updated_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    error text,
    PRIMARY KEY (organization_id, build_id),
    FOREIGN KEY (build_id, organization_id) REFERENCES builds(id, organization_id)
);
ALTER TABLE build_retention ENABLE ROW LEVEL SECURITY;
ALTER TABLE build_retention FORCE ROW LEVEL SECURITY;
CREATE POLICY build_retention_tenant ON build_retention
    USING (organization_id = fogell_current_organization_id())
    WITH CHECK (organization_id = fogell_current_organization_id());

-- Retention preserves request digests before removing potentially large source
-- envelopes. Every other delete/update remains forbidden by the original law.
CREATE OR REPLACE FUNCTION fogell_guard_build_definition() RETURNS trigger LANGUAGE plpgsql
SET search_path=pg_catalog,pg_temp AS $$
BEGIN
    IF TG_OP = 'DELETE' THEN
        IF EXISTS (SELECT 1 FROM public.build_retention r WHERE r.organization_id=OLD.organization_id
          AND r.build_id=OLD.build_id AND r.state='deleting'
          AND r.source_digest=OLD.source_digest AND r.admission_fingerprint=OLD.admission_fingerprint) THEN
            RETURN OLD;
        END IF;
        RAISE EXCEPTION 'build definitions cannot be deleted outside verified retention';
    ELSIF ROW(NEW.build_id,NEW.organization_id,NEW.source_bytes,NEW.source_digest,NEW.admission_fingerprint,NEW.created_at)
      IS DISTINCT FROM ROW(OLD.build_id,OLD.organization_id,OLD.source_bytes,OLD.source_digest,OLD.admission_fingerprint,OLD.created_at) THEN
        RAISE EXCEPTION 'build definitions are immutable';
    END IF;
    RETURN NEW;
END $$;

-- Take the same build lock as selection before checking its tombstone. This
-- covers direct SQL callers too, including retry insertion after an earlier
-- application-level eligibility read.
CREATE FUNCTION fogell_retention_guard_write() RETURNS trigger LANGUAGE plpgsql
SET search_path=pg_catalog,pg_temp AS $$
DECLARE target uuid;
BEGIN
    IF TG_TABLE_NAME = 'attempts' THEN
        SELECT build_id INTO STRICT target FROM public.nodes
          WHERE id=NEW.node_id AND organization_id=NEW.organization_id;
    ELSIF TG_TABLE_NAME = 'retry_decisions' THEN
        SELECT n.build_id INTO STRICT target FROM public.attempts a JOIN public.nodes n
          ON n.id=a.node_id AND n.organization_id=a.organization_id
          WHERE a.id=NEW.parent_attempt_id AND a.organization_id=NEW.organization_id;
    ELSIF TG_TABLE_NAME = 'builds' THEN
        target := NEW.id;
    ELSE
        target := NEW.build_id;
    END IF;
    PERFORM 1 FROM public.builds WHERE id=target AND organization_id=NEW.organization_id FOR UPDATE;
    IF EXISTS (SELECT 1 FROM public.build_retention WHERE organization_id=NEW.organization_id AND build_id=target) THEN
        RAISE EXCEPTION 'evidence_expired: retention has selected this build';
    END IF;
    RETURN NEW;
END $$;
CREATE TRIGGER attempts_retention_insert BEFORE INSERT OR UPDATE ON attempts
    FOR EACH ROW EXECUTE FUNCTION fogell_retention_guard_write();
CREATE TRIGGER logs_retention_insert BEFORE INSERT ON log_chunks
    FOR EACH ROW EXECUTE FUNCTION fogell_retention_guard_write();
-- Effect writes already require live attempt authority; confirmed checkpoints
-- cannot become uncertain. Retention holds attempt locks and excludes every
-- nonconfirmed checkpoint. Adding a build lock to checkpoint classification
-- would invert restore/classification ordering and block before its org lock.
CREATE TRIGGER retry_retention_insert BEFORE INSERT ON retry_decisions
    FOR EACH ROW EXECUTE FUNCTION fogell_retention_guard_write();
CREATE TRIGGER builds_retention_update BEFORE UPDATE ON builds
    FOR EACH ROW WHEN (NEW.status IS DISTINCT FROM OLD.status)
    EXECUTE FUNCTION fogell_retention_guard_write();

-- Runtime roles need only SELECT on tombstones; the dedicated maintenance
-- operator owns INSERT/UPDATE/DELETE of payload and deletion-journal rows.
