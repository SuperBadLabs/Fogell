-- FG-026b (Codex #424 round 13). The uncertain listing and its keyset cursor
-- led with uncertain_at, a wall-clock instant: the per-organization advisory
-- lock (round 12) orders classification transactions, but statement_timestamp()
-- is not monotonic across an NTP or VM clock step backward, and two
-- classifications inside timestamp precision share an instant. Either way a
-- later serialized classification could carry an uncertain_at at or before a
-- cursor already issued, and the cursor's strict keyset skipped that row
-- forever. The cursor now leads with a database-generated classification
-- sequence: uncertain_seq is NULL until the row is classified and is assigned
-- from this sequence by the classification statement under the organization's
-- lock, so within an organization it is monotone with commit order whatever
-- the clock does. Across organizations the sequence merely interleaves, which
-- a per-organization cursor never observes.
CREATE SEQUENCE effect_checkpoints_uncertain_seq AS bigint;

ALTER TABLE effect_checkpoints
    ADD COLUMN uncertain_seq bigint;

-- Backfill every row already uncertain in the pre-0014 listing order, so the
-- listing is unchanged for rows classified before this migration. The table is
-- FORCE-RLS and migrations run as the maintenance owner outside a tenant
-- transaction, so the policy is lifted for the backfill exactly as 0008 did;
-- the 0003 guard refuses every update of an uncertain row (the state is
-- terminal), so it is disabled for the same statement and re-enabled before
-- this transaction can commit. row_number() rather than nextval() keeps the
-- backfill deterministic; the sequence is then advanced past the last value
-- assigned (and left untouched when nothing was uncertain).
ALTER TABLE effect_checkpoints NO FORCE ROW LEVEL SECURITY;
ALTER TABLE effect_checkpoints DISABLE ROW LEVEL SECURITY;
ALTER TABLE effect_checkpoints DISABLE TRIGGER effect_checkpoints_guard;

WITH ordered AS (
    SELECT organization_id,
           attempt_id,
           effect_key,
           row_number() OVER (
               ORDER BY uncertain_at, prepared_at, attempt_id, effect_key
           ) AS uncertain_seq
      FROM effect_checkpoints
     WHERE state = 'uncertain'
)
UPDATE effect_checkpoints e
   SET uncertain_seq = o.uncertain_seq
  FROM ordered o
 WHERE e.organization_id = o.organization_id
   AND e.attempt_id = o.attempt_id
   AND e.effect_key = o.effect_key;

SELECT setval(
    'effect_checkpoints_uncertain_seq',
    coalesce(max(uncertain_seq), 1),
    max(uncertain_seq) IS NOT NULL)
  FROM effect_checkpoints;

ALTER TABLE effect_checkpoints ENABLE TRIGGER effect_checkpoints_guard;
ALTER TABLE effect_checkpoints ENABLE ROW LEVEL SECURITY;
ALTER TABLE effect_checkpoints FORCE ROW LEVEL SECURITY;

-- A row is uncertain exactly when it carries a classification sequence. The
-- 0003 guard already makes every uncertain row immutable, so the sequence can
-- never change once assigned. Spelled in its stable deparsed form so the
-- FG-085a restored schema inventory stays byte-identical.
ALTER TABLE effect_checkpoints
    ADD CONSTRAINT effect_checkpoints_uncertain_seq_check
        CHECK ((state = 'uncertain') = (uncertain_seq IS NOT NULL));

-- The 0012 index served the (uncertain_at, prepared_at, attempt_id,
-- effect_key) keyset; nothing orders by it any more. The replacement matches
-- the new listing order exactly, so a page is an ordered index scan with no
-- Sort node, and it is UNIQUE because the cursor's strict `>` rests on the
-- sequence never repeating. 0013's live-authority index is untouched.
DROP INDEX effect_checkpoints_uncertain_order;

CREATE UNIQUE INDEX effect_checkpoints_uncertain_sequence
    ON effect_checkpoints (organization_id, uncertain_seq)
    WHERE state = 'uncertain';
