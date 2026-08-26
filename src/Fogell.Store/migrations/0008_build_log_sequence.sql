-- Progressive log cursors are build-wide, while execution attempts (including
-- retries and parallel nodes) number their own frames from zero. Keep the
-- attempt-local sequence for idempotent publication and add a separately
-- allocated build-wide sequence for the public cursor.

-- Migrations run as the maintenance owner, outside a tenant transaction. The
-- two tables are FORCE-RLS in normal operation, so temporarily disable their
-- policies while backfilling every tenant and restore the boundary before this
-- transaction can commit.
ALTER TABLE builds NO FORCE ROW LEVEL SECURITY;
ALTER TABLE builds DISABLE ROW LEVEL SECURITY;
ALTER TABLE log_chunks NO FORCE ROW LEVEL SECURITY;
ALTER TABLE log_chunks DISABLE ROW LEVEL SECURITY;

ALTER TABLE builds
    ADD COLUMN next_log_sequence integer NOT NULL DEFAULT 0;

ALTER TABLE log_chunks
    ADD COLUMN build_sequence integer;

WITH ordered AS (
    SELECT id,
           organization_id,
           build_id,
           sequence,
           row_number() OVER (
               PARTITION BY organization_id, build_id
               ORDER BY created_at, id
           ) - 1 AS ordinal
      FROM log_chunks
),
ranked AS (
    SELECT id,
           ordinal + MAX(sequence - ordinal) OVER (
               PARTITION BY organization_id, build_id
               ORDER BY ordinal
               ROWS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW
           ) AS build_sequence
      FROM ordered
)
UPDATE log_chunks AS chunk
   SET build_sequence = ranked.build_sequence
  FROM ranked
 WHERE ranked.id = chunk.id;

UPDATE builds AS build
   SET next_log_sequence = next_sequence.value
  FROM (
      SELECT organization_id,
             build_id,
             COALESCE(MAX(build_sequence), -1) + 1 AS value
        FROM log_chunks
       GROUP BY organization_id, build_id
  ) AS next_sequence
 WHERE build.organization_id = next_sequence.organization_id
   AND build.id = next_sequence.build_id;

ALTER TABLE log_chunks
    ALTER COLUMN build_sequence SET NOT NULL;

ALTER TABLE log_chunks
    ADD CONSTRAINT log_chunks_build_sequence_unique
    UNIQUE (organization_id, build_id, build_sequence);

DROP INDEX IF EXISTS log_chunks_read_order;
CREATE INDEX log_chunks_read_order
    ON log_chunks (organization_id, build_id, build_sequence);

ALTER TABLE builds ENABLE ROW LEVEL SECURITY;
ALTER TABLE builds FORCE ROW LEVEL SECURITY;
ALTER TABLE log_chunks ENABLE ROW LEVEL SECURITY;
ALTER TABLE log_chunks FORCE ROW LEVEL SECURITY;
