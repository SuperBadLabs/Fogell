-- Repair organization roots missed by the original runnable-controller
-- backfill. Migration 0005 FORCEs tenant RLS, so a maintenance owner without
-- BYPASSRLS sees no organizations unless that protection is explicitly lowered
-- for the backfill transaction. Restore the boundary before commit, matching
-- the populated-data migration pattern in 0008.
ALTER TABLE organizations NO FORCE ROW LEVEL SECURITY;
ALTER TABLE organizations DISABLE ROW LEVEL SECURITY;

INSERT INTO organization_work_roots (organization_id)
SELECT id FROM organizations
ON CONFLICT (organization_id) DO NOTHING;

ALTER TABLE organizations ENABLE ROW LEVEL SECURITY;
ALTER TABLE organizations FORCE ROW LEVEL SECURITY;
