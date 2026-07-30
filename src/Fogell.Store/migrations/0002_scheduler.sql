-- FG-061. Scheduler support.
--
-- The claim index is tenant-prefixed because every query is tenant-scoped: an
-- index that leads with `state` would scan across organizations before
-- filtering, which is both slower and a cross-tenant read pattern.
CREATE INDEX IF NOT EXISTS attempts_offerable
    ON attempts (organization_id, state, created_at, id)
    WHERE state = 'queued';

-- Nodes carry the requirements a claim must satisfy.
CREATE INDEX IF NOT EXISTS nodes_claim_order
    ON nodes (organization_id, required_trust_pool, ordinal, id);

-- Log lines, appended as a step produces them (FG-064 progressive console).
CREATE TABLE IF NOT EXISTS log_chunks (
    id              bigserial PRIMARY KEY,
    organization_id uuid NOT NULL,
    build_id        uuid NOT NULL,
    attempt_id      uuid NOT NULL,
    sequence        integer NOT NULL,
    body            text NOT NULL,
    created_at      timestamptz NOT NULL DEFAULT clock_timestamp(),
    FOREIGN KEY (build_id, organization_id) REFERENCES builds (id, organization_id),
    UNIQUE (organization_id, attempt_id, sequence)
);

CREATE INDEX IF NOT EXISTS log_chunks_read_order
    ON log_chunks (organization_id, build_id, sequence);
