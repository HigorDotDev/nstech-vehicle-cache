-- Source of truth: docs/02-spec.md §3. Loaded by the postgres image on first start (docker-entrypoint-initdb.d).
-- The triggers enforce, at the source, the preconditions the cache's convergence argument relies on (spec §6):
-- one payload per (id, version), immutable ids, ids never reused, append-only positions.

CREATE TABLE vehicles (
    id               bigint      PRIMARY KEY,             -- externally assigned fleet id, never reused (ADR-0004)
    plate            text        NOT NULL,
    state            text        NOT NULL CHECK (state IN ('REGISTERED','ACTIVE','IDLE','MAINTENANCE','DECOMMISSIONED')),
    state_changed_at timestamptz NOT NULL,                -- business time, informational for readers
    version          bigint      NOT NULL DEFAULT 1       -- ordering key (ADR-0002), owned by the trigger below
);

-- Ids of deleted vehicles. Maintained by trigger, NOT captured by Debezium, never read by the worker.
-- Blocks id reuse and is the verifier oracle's source for "deleted" vs "never existed".
CREATE TABLE retired_vehicle_ids (
    vehicle_id bigint      PRIMARY KEY,
    retired_at timestamptz NOT NULL DEFAULT now()
);

CREATE FUNCTION vehicles_guard() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF TG_OP = 'INSERT' THEN
        IF EXISTS (SELECT 1 FROM retired_vehicle_ids WHERE vehicle_id = NEW.id) THEN
            RAISE EXCEPTION 'vehicle id % was deleted and cannot be reused', NEW.id;
        END IF;
        NEW.version := 1;                    -- writers cannot choose the version
    ELSE
        IF NEW.id <> OLD.id THEN
            RAISE EXCEPTION 'vehicle id is immutable (% -> %)', OLD.id, NEW.id;
        END IF;
        NEW.version := OLD.version + 1;      -- writers cannot set or regress it
    END IF;
    RETURN NEW;
END $$;

CREATE TRIGGER trg_vehicles_guard BEFORE INSERT OR UPDATE ON vehicles
    FOR EACH ROW EXECUTE FUNCTION vehicles_guard();

CREATE FUNCTION vehicles_retire() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    INSERT INTO retired_vehicle_ids (vehicle_id) VALUES (OLD.id);
    RETURN OLD;
END $$;

CREATE TRIGGER trg_vehicles_retire AFTER DELETE ON vehicles
    FOR EACH ROW EXECUTE FUNCTION vehicles_retire();

CREATE TABLE positions (
    id          bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    vehicle_id  bigint           NOT NULL,                -- deliberately no foreign key (ADR-0003)
    -- double, not numeric: avoids Debezium base64 decimals. The range checks also reject NaN/±Infinity, which
    -- would reach the worker as non-JSON-number tokens and halt the positions consumer forever (poison).
    lat         double precision NOT NULL CHECK (lat BETWEEN -90 AND 90),
    lng         double precision NOT NULL CHECK (lng BETWEEN -180 AND 180),
    recorded_at timestamptz      NOT NULL,                -- business (event) time — ordering key
    inserted_at timestamptz      NOT NULL DEFAULT now()   -- ingestion time — never used for ordering
);

CREATE INDEX ix_positions_latest ON positions (vehicle_id, recorded_at DESC, id DESC);  -- verifier oracle only

-- Positions are facts: an UPDATE/DELETE would move the argmax in a way a history-less cache cannot follow.
CREATE FUNCTION positions_append_only() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION 'positions are append-only (% rejected)', TG_OP;
END $$;

CREATE TRIGGER trg_positions_append_only BEFORE UPDATE OR DELETE ON positions
    FOR EACH ROW EXECUTE FUNCTION positions_append_only();

-- TRUNCATE is not a supported source operation for either captured table (spec §4).
CREATE TRIGGER trg_positions_no_truncate BEFORE TRUNCATE ON positions
    FOR EACH STATEMENT EXECUTE FUNCTION positions_append_only();

CREATE FUNCTION vehicles_no_truncate() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION 'TRUNCATE on vehicles is not supported: delete rows so CDC emits tombstones';
END $$;

CREATE TRIGGER trg_vehicles_no_truncate BEFORE TRUNCATE ON vehicles
    FOR EACH STATEMENT EXECUTE FUNCTION vehicles_no_truncate();
