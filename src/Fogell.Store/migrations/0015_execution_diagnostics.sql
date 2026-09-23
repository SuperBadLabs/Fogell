-- Additive: old binaries ignore this nullable column; rollback leaves data intact.
-- Restore the matching DB/application pair if an operational rollback is required.
ALTER TABLE log_chunks ADD COLUMN diagnostic jsonb NULL;
ALTER TABLE log_chunks ADD CONSTRAINT bounded_diagnostic
    CHECK (diagnostic IS NULL OR
        (jsonb_typeof(diagnostic) = 'object' AND octet_length(diagnostic::text) <= 16384));
