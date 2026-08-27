-- FG-224. Make the restore/admission serialization a database invariant.
-- Every attempt creator, including maintenance SQL outside Store, must take the
-- shared side of the singleton epoch lock and stamp the exact current value.
-- Restore updates that singleton before invalidating attempts, so an insert
-- either commits first and is invalidated, or waits and is born current.
CREATE FUNCTION fogell_guard_attempt_restore_epoch()
RETURNS trigger
LANGUAGE plpgsql
SET search_path = pg_catalog, pg_temp
AS $$
DECLARE
    current_epoch bigint;
BEGIN
    SELECT restore_epoch
      INTO current_epoch
      FROM public.controller_metadata
     WHERE singleton
     FOR SHARE;

    IF NEW.restore_epoch IS DISTINCT FROM current_epoch THEN
        RAISE EXCEPTION
            'attempt restore_epoch % does not match current controller restore_epoch %',
            NEW.restore_epoch,
            current_epoch
            USING ERRCODE = '23514';
    END IF;

    RETURN NEW;
END;
$$;

CREATE TRIGGER attempts_restore_epoch_guard
BEFORE INSERT ON attempts
FOR EACH ROW
EXECUTE FUNCTION fogell_guard_attempt_restore_epoch();
