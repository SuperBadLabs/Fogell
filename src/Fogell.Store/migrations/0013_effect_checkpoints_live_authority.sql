-- FG-026b (pre-review verifier P2-2 on #424). The classification pass
-- (Store.markStaleEffects) selects an organization's prepared/applied
-- checkpoints -- the rows whose authority can go stale -- on every
-- reconciliation cadence tick and at startup. No index covered that predicate:
-- on 200,000 confirmed rows the pass was a sequential scan of 5,715 buffers
-- (23.8 ms); with this partial index it is an index scan of 2 buffers
-- (0.05 ms). The predicate is spelled in its stable deparsed form so the
-- FG-085a restored schema inventory stays byte-identical.
CREATE INDEX effect_checkpoints_live_authority
    ON effect_checkpoints (organization_id, attempt_id)
    WHERE state IN ('prepared', 'applied');
