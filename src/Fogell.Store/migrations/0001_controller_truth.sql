-- FG-020 / ADR 0007. Controller truth.
--
-- Every constraint here encodes a defect class observed in an engine this one
-- replaces. Composite foreign keys are not decoration: they make cross-tenant
-- parent substitution unrepresentable rather than merely forbidden.

CREATE TABLE IF NOT EXISTS organizations (
    id   uuid PRIMARY KEY,
    slug text NOT NULL UNIQUE
);

CREATE TABLE IF NOT EXISTS projects (
    id              uuid NOT NULL,
    organization_id uuid NOT NULL REFERENCES organizations (id),
    slug            text NOT NULL,
    PRIMARY KEY (id, organization_id),
    UNIQUE (organization_id, slug)
);

-- Singleton row carrying the controller restore epoch. Bumping it invalidates
-- every lease issued before a restore, so a pre-restore agent cannot publish.
CREATE TABLE IF NOT EXISTS controller_metadata (
    singleton     boolean PRIMARY KEY DEFAULT true CHECK (singleton),
    restore_epoch bigint  NOT NULL DEFAULT 0
);
INSERT INTO controller_metadata (singleton, restore_epoch)
VALUES (true, 0)
ON CONFLICT (singleton) DO NOTHING;

CREATE TABLE IF NOT EXISTS builds (
    id                     uuid    NOT NULL,
    organization_id        uuid    NOT NULL,
    project_id             uuid    NOT NULL,
    number                 integer NOT NULL,
    idempotency_key        text    NOT NULL,
    status                 text    NOT NULL,
    cancellation_requested boolean NOT NULL DEFAULT false,
    created_at             timestamptz NOT NULL DEFAULT clock_timestamp(),
    PRIMARY KEY (id, organization_id),
    FOREIGN KEY (project_id, organization_id) REFERENCES projects (id, organization_id),
    -- FG-021: admission is idempotent per project, enforced by the database
    -- rather than by a check-then-insert race in application code.
    UNIQUE (organization_id, project_id, idempotency_key),
    UNIQUE (organization_id, project_id, number)
);

CREATE TABLE IF NOT EXISTS nodes (
    id                       uuid NOT NULL,
    organization_id          uuid NOT NULL,
    build_id                 uuid NOT NULL,
    name                     text NOT NULL,
    ordinal                  integer NOT NULL,
    required_trust_pool      text NOT NULL,
    required_capabilities    text[] NOT NULL DEFAULT '{}',
    status                   text NOT NULL,
    PRIMARY KEY (id, organization_id),
    FOREIGN KEY (build_id, organization_id) REFERENCES builds (id, organization_id)
);

CREATE TABLE IF NOT EXISTS attempts (
    id               uuid   NOT NULL,
    organization_id  uuid   NOT NULL,
    node_id          uuid   NOT NULL,
    ordinal          integer NOT NULL,
    retry_of         uuid,
    state            text   NOT NULL,
    -- FG-022: monotonic guard. Only the holder of the exact current fence may
    -- publish a terminal result.
    fence            bigint NOT NULL DEFAULT 0,
    restore_epoch    bigint NOT NULL DEFAULT 0,
    lease_owner      text,
    lease_expires_at timestamptz,
    result           text,
    created_at       timestamptz NOT NULL DEFAULT clock_timestamp(),
    PRIMARY KEY (id, organization_id),
    FOREIGN KEY (node_id, organization_id) REFERENCES nodes (id, organization_id),
    FOREIGN KEY (retry_of, organization_id) REFERENCES attempts (id, organization_id),
    -- an attempt with a lease must say when it expires
    CHECK ((lease_owner IS NULL) = (lease_expires_at IS NULL)),
    UNIQUE (organization_id, node_id, ordinal)
);

-- Durable, append-only history. Never updated.
CREATE TABLE IF NOT EXISTS events (
    id              bigserial PRIMARY KEY,
    organization_id uuid NOT NULL,
    build_id        uuid NOT NULL,
    attempt_id      uuid,
    kind            text NOT NULL,
    payload         jsonb NOT NULL DEFAULT '{}',
    created_at      timestamptz NOT NULL DEFAULT clock_timestamp(),
    FOREIGN KEY (build_id, organization_id) REFERENCES builds (id, organization_id)
);

-- Transactional outbox: a message is committed with the state change that
-- produced it, so there is no window where one exists without the other.
CREATE TABLE IF NOT EXISTS outbox (
    id              bigserial PRIMARY KEY,
    organization_id uuid NOT NULL,
    topic           text NOT NULL,
    body            jsonb NOT NULL,
    published_at    timestamptz,
    created_at      timestamptz NOT NULL DEFAULT clock_timestamp()
);

CREATE INDEX IF NOT EXISTS attempts_claim_order
    ON attempts (organization_id, state, created_at, id);

CREATE INDEX IF NOT EXISTS outbox_unpublished
    ON outbox (created_at) WHERE published_at IS NULL;
