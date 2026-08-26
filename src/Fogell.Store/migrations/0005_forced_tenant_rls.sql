-- FG-028. PostgreSQL, not caller discipline, is the tenant boundary.
--
-- A tenant is selected only inside a transaction with:
--   SELECT set_config('fogell.organization_id', '<uuid>', true);
-- `true` makes the setting transaction-local. Missing context resolves to NULL,
-- so every policy below exposes zero rows. Invalid context fails closed while
-- casting to uuid.

CREATE FUNCTION fogell_current_organization_id()
RETURNS uuid
LANGUAGE sql
STABLE
PARALLEL SAFE
AS $$
    SELECT NULLIF(current_setting('fogell.organization_id', true), '')::uuid
$$;

-- Complete the tenant-composite lineage where the early schema carried only
-- the build relation. Nullable event attempt ids remain valid for build-level
-- events; when present, they must belong to the same organization.
ALTER TABLE events
    ADD CONSTRAINT events_attempt_tenant_fk
    FOREIGN KEY (attempt_id, organization_id)
    REFERENCES attempts (id, organization_id);

ALTER TABLE log_chunks
    ADD CONSTRAINT log_chunks_attempt_tenant_fk
    FOREIGN KEY (attempt_id, organization_id)
    REFERENCES attempts (id, organization_id);

ALTER TABLE organizations ENABLE ROW LEVEL SECURITY;
ALTER TABLE organizations FORCE ROW LEVEL SECURITY;
CREATE POLICY organizations_tenant_isolation ON organizations
    USING (id = fogell_current_organization_id())
    WITH CHECK (id = fogell_current_organization_id());

ALTER TABLE projects ENABLE ROW LEVEL SECURITY;
ALTER TABLE projects FORCE ROW LEVEL SECURITY;
CREATE POLICY projects_tenant_isolation ON projects
    USING (organization_id = fogell_current_organization_id())
    WITH CHECK (organization_id = fogell_current_organization_id());

ALTER TABLE builds ENABLE ROW LEVEL SECURITY;
ALTER TABLE builds FORCE ROW LEVEL SECURITY;
CREATE POLICY builds_tenant_isolation ON builds
    USING (organization_id = fogell_current_organization_id())
    WITH CHECK (organization_id = fogell_current_organization_id());

ALTER TABLE nodes ENABLE ROW LEVEL SECURITY;
ALTER TABLE nodes FORCE ROW LEVEL SECURITY;
CREATE POLICY nodes_tenant_isolation ON nodes
    USING (organization_id = fogell_current_organization_id())
    WITH CHECK (organization_id = fogell_current_organization_id());

ALTER TABLE attempts ENABLE ROW LEVEL SECURITY;
ALTER TABLE attempts FORCE ROW LEVEL SECURITY;
CREATE POLICY attempts_tenant_isolation ON attempts
    USING (organization_id = fogell_current_organization_id())
    WITH CHECK (organization_id = fogell_current_organization_id());

ALTER TABLE events ENABLE ROW LEVEL SECURITY;
ALTER TABLE events FORCE ROW LEVEL SECURITY;
CREATE POLICY events_tenant_isolation ON events
    USING (organization_id = fogell_current_organization_id())
    WITH CHECK (organization_id = fogell_current_organization_id());

ALTER TABLE outbox ENABLE ROW LEVEL SECURITY;
ALTER TABLE outbox FORCE ROW LEVEL SECURITY;
CREATE POLICY outbox_tenant_isolation ON outbox
    USING (organization_id = fogell_current_organization_id())
    WITH CHECK (organization_id = fogell_current_organization_id());

ALTER TABLE log_chunks ENABLE ROW LEVEL SECURITY;
ALTER TABLE log_chunks FORCE ROW LEVEL SECURITY;
CREATE POLICY log_chunks_tenant_isolation ON log_chunks
    USING (organization_id = fogell_current_organization_id())
    WITH CHECK (organization_id = fogell_current_organization_id());

ALTER TABLE effect_checkpoints ENABLE ROW LEVEL SECURITY;
ALTER TABLE effect_checkpoints FORCE ROW LEVEL SECURITY;
CREATE POLICY effect_checkpoints_tenant_isolation ON effect_checkpoints
    USING (organization_id = fogell_current_organization_id())
    WITH CHECK (organization_id = fogell_current_organization_id());

ALTER TABLE retry_decisions ENABLE ROW LEVEL SECURITY;
ALTER TABLE retry_decisions FORCE ROW LEVEL SECURITY;
CREATE POLICY retry_decisions_tenant_isolation ON retry_decisions
    USING (organization_id = fogell_current_organization_id())
    WITH CHECK (organization_id = fogell_current_organization_id());
