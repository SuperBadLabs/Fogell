-- FG-267. Snapshot transport stays in immutable build_definitions, whose raw
-- digest/fingerprint binds the complete manifest under existing idempotency.
-- Only bounded, credential-free verified provenance is retained here.
CREATE TABLE source_verifications (
    organization_id uuid NOT NULL,
    build_id uuid NOT NULL,
    attempt_id uuid NOT NULL,
    fence bigint NOT NULL,
    identity_json jsonb NOT NULL CHECK (octet_length(identity_json::text) <= 4096),
    verified_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    PRIMARY KEY (organization_id, attempt_id),
    FOREIGN KEY (build_id, organization_id) REFERENCES builds(id, organization_id),
    FOREIGN KEY (attempt_id, organization_id) REFERENCES attempts(id, organization_id)
);
ALTER TABLE source_verifications ENABLE ROW LEVEL SECURITY;
ALTER TABLE source_verifications FORCE ROW LEVEL SECURITY;
CREATE POLICY source_verifications_tenant_isolation ON source_verifications
    USING (organization_id = nullif(current_setting('fogell.organization_id', true), '')::uuid)
    WITH CHECK (organization_id = nullif(current_setting('fogell.organization_id', true), '')::uuid);
