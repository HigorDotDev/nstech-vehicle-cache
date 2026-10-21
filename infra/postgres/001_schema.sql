-- Source of truth: docs/02-spec.md §3. Loaded by the postgres image on first start (docker-entrypoint-initdb.d).

CREATE TABLE vehicles (
    id               bigint      PRIMARY KEY,             -- externally assigned fleet id, never reused (ADR-0004)
    plate            text        NOT NULL,
    state            text        NOT NULL CHECK (state IN ('REGISTERED','ACTIVE','IDLE','MAINTENANCE','DECOMMISSIONED')),
    state_changed_at timestamptz NOT NULL,                -- business time, informational for readers
    version          bigint      NOT NULL DEFAULT 1       -- ordering key (ADR-0002), owned by the trigger below
);

CREATE FUNCTION vehicles_bump_version() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    NEW.version := OLD.version + 1;   -- writers cannot set or regress it
    RETURN NEW;
END $$;

CREATE TRIGGER trg_vehicles_version BEFORE UPDATE ON vehicles
    FOR EACH ROW EXECUTE FUNCTION vehicles_bump_version();

CREATE TABLE positions (
    id          bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    vehicle_id  bigint           NOT NULL,                -- deliberately no foreign key (ADR-0003)
    lat         double precision NOT NULL,                -- double, not numeric: avoids Debezium base64 decimals
    lng         double precision NOT NULL,
    recorded_at timestamptz      NOT NULL,                -- business (event) time — ordering key
    inserted_at timestamptz      NOT NULL DEFAULT now()   -- ingestion time — never used for ordering
);

CREATE INDEX ix_positions_latest ON positions (vehicle_id, recorded_at DESC, id DESC);  -- verifier oracle only

-- Test harness only: not in table.include.list, never read by the worker.
-- Lets the oracle tell "deleted" from "never existed" after the row is gone.
CREATE TABLE writer_deletions (
    vehicle_id bigint      PRIMARY KEY,
    deleted_at timestamptz NOT NULL DEFAULT now()
);
