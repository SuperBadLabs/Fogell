-- FG-026b (Codex #424 round 7). The operator listing of uncertain effects and
-- its keyset cursor order by (uncertain_at, prepared_at, attempt_id,
-- effect_key): uncertain_at is monotone for rows entering the set, so a page
-- never skips a row classified behind an issued cursor, and prepared_at breaks
-- the tie for rows classified in one pass. The only partial index, from 0003,
-- led with prepared_at, so a small page had to sort an organization's whole
-- uncertain history. This index matches the listing order exactly, so a page
-- is an ordered index scan with no Sort node.
--
-- The 0003 index is dropped here because nothing uses it any more: the
-- classification pass filters state IN ('prepared', 'applied'), which its
-- WHERE state = 'uncertain' predicate never covers, and both listings now
-- order by uncertain_at first. The predicate is spelled as 0003 spelled it,
-- so the restored-schema inventory of the FG-085a drill stays byte-identical.
DROP INDEX IF EXISTS effect_checkpoints_uncertain;

CREATE INDEX effect_checkpoints_uncertain_order
    ON effect_checkpoints (organization_id, uncertain_at, prepared_at, attempt_id, effect_key)
    WHERE state = 'uncertain';
