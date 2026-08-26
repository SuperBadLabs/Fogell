-- FG-085a. PostgreSQL expands BETWEEN to a grouped pair of comparisons when
-- the original table is dumped, then flattens the equivalent AND expression
-- after a custom-archive restore.  Spell the constraint in its stable deparsed
-- form so source and restored schema inventories are byte-identical.
ALTER TABLE effect_checkpoints
    DROP CONSTRAINT effect_checkpoints_effect_key_check,
    ADD CONSTRAINT effect_checkpoints_effect_key_check
        CHECK (
            char_length(effect_key) >= 1
            AND char_length(effect_key) <= 256
            AND btrim(effect_key) <> ''
        );
