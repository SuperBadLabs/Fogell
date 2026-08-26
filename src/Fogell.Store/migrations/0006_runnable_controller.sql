-- FG-224. Forced tenant RLS deliberately makes organizations invisible until
-- a transaction selects one tenant.  A restartable worker still needs a way
-- to discover which tenant scopes to visit, so this global registry contains
-- UUID roots only: no slug and no tenant record.  The trigger is security
-- definer so an RLS-bound runtime role can create an organization without
-- receiving a general write capability on the global registry.
CREATE TABLE organization_work_roots (
    organization_id uuid PRIMARY KEY REFERENCES organizations (id) ON DELETE CASCADE
);

INSERT INTO organization_work_roots (organization_id)
SELECT id FROM organizations;

CREATE FUNCTION fogell_register_organization_work_root()
RETURNS trigger
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog, pg_temp
AS $$
BEGIN
    INSERT INTO public.organization_work_roots (organization_id)
    VALUES (NEW.id)
    ON CONFLICT DO NOTHING;
    RETURN NEW;
END;
$$;

REVOKE ALL ON FUNCTION fogell_register_organization_work_root() FROM PUBLIC;

CREATE TRIGGER organizations_register_work_root
AFTER INSERT ON organizations
FOR EACH ROW
EXECUTE FUNCTION fogell_register_organization_work_root();

-- The accepted definition is controller truth, not request-lifetime
-- memory.  Keeping it in a separate append-only relation lets pre-FG-224
-- builds remain readable while every new admission is required by Store to
-- create exactly one immutable definition in the same transaction.
CREATE TABLE build_definitions (
    build_id              uuid NOT NULL,
    organization_id       uuid NOT NULL,
    source_bytes          bytea NOT NULL,
    source_digest         bytea NOT NULL CHECK (octet_length(source_digest) = 32),
    admission_fingerprint bytea NOT NULL CHECK (octet_length(admission_fingerprint) = 32),
    created_at            timestamptz NOT NULL DEFAULT clock_timestamp(),
    PRIMARY KEY (build_id, organization_id),
    FOREIGN KEY (build_id, organization_id)
        REFERENCES builds (id, organization_id)
);

ALTER TABLE build_definitions ENABLE ROW LEVEL SECURITY;
ALTER TABLE build_definitions FORCE ROW LEVEL SECURITY;

CREATE POLICY build_definitions_tenant_isolation ON build_definitions
    USING (organization_id = nullif(current_setting('fogell.organization_id', true), '')::uuid)
    WITH CHECK (organization_id = nullif(current_setting('fogell.organization_id', true), '')::uuid);

CREATE FUNCTION fogell_guard_build_definition()
RETURNS trigger
LANGUAGE plpgsql
AS $$
BEGIN
    IF TG_OP = 'DELETE' THEN
        RAISE EXCEPTION 'build definitions cannot be deleted';
    ELSIF ROW(NEW.build_id, NEW.organization_id, NEW.source_bytes,
              NEW.source_digest, NEW.admission_fingerprint, NEW.created_at)
          IS DISTINCT FROM
          ROW(OLD.build_id, OLD.organization_id, OLD.source_bytes,
              OLD.source_digest, OLD.admission_fingerprint, OLD.created_at) THEN
        RAISE EXCEPTION 'build definitions are immutable';
    END IF;

    RETURN NEW;
END;
$$;

CREATE TRIGGER build_definitions_guard
BEFORE UPDATE OR DELETE ON build_definitions
FOR EACH ROW
EXECUTE FUNCTION fogell_guard_build_definition();

CREATE INDEX build_definitions_tenant_build
    ON build_definitions (organization_id, build_id);
