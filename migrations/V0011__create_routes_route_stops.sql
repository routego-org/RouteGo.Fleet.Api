-- =============================================================================
-- V0011__create_routes_route_stops.sql
-- One city on a route, in order, with the minutes after departure it is reached.
-- =============================================================================
-- Implements: data-model/routes/route-stop.md v0.1.0
-- Change tracking: audited (the default) - eight audit columns + audit.fn_enable_audit.
-- Identifier: natural key (route_id, sequence) per the entity's Identity; the audit log records
--   route_id as the row id, so a route's whole history is one lookup.
-- "Offsets strictly increase with the sequence, starting from 0" and "sequence without gaps"
--   compare rows with each other, so the API enforces them; the database enforces each row's range.

BEGIN;

CREATE TABLE routes.route_stops (
    route_id         uuid        NOT NULL,
    sequence         smallint    NOT NULL,
    city_id          uuid        NOT NULL,
    offset_minutes   smallint    NOT NULL,

    -- Audit columns: populated by the audit trigger; declared NOT NULL here.
    -- The *_by_id columns are text (not uuid) - the actor id is an opaque string from the
    -- authenticating issuer, capped at 64 chars by the CHECKs below.
    created_by_id    text        NOT NULL,
    created_by_email text        NOT NULL,
    created_by_name  text        NOT NULL,
    created_at       timestamptz NOT NULL,
    updated_by_id    text        NOT NULL,
    updated_by_email text        NOT NULL,
    updated_by_name  text        NOT NULL,
    updated_at       timestamptz NOT NULL,
    CONSTRAINT pk_route_stops PRIMARY KEY (route_id, sequence),
    -- Relationship: "Belongs to one Route."
    CONSTRAINT fk_route_stops_route_id FOREIGN KEY (route_id) REFERENCES routes.routes (route_id),
    -- Relationship: "Names one City."
    CONSTRAINT fk_route_stops_city_id FOREIGN KEY (city_id) REFERENCES routes.cities (city_id),
    -- Rule: "No city appears twice on a route."
    CONSTRAINT uq_route_stops_route_id_city_id UNIQUE (route_id, city_id),
    -- Rule: a route has at most 12 stops, numbered from 1.
    CONSTRAINT ck_route_stops_sequence CHECK (sequence BETWEEN 1 AND 12),
    -- Rule: offsets are whole minutes, 0 to 2,880 (48 hours).
    CONSTRAINT ck_route_stops_offset_minutes CHECK (offset_minutes BETWEEN 0 AND 2880),
    CONSTRAINT ck_route_stops_created_by_id_len CHECK (length(created_by_id) <= 64),
    CONSTRAINT ck_route_stops_updated_by_id_len CHECK (length(updated_by_id) <= 64)
);

-- The city foreign key is checked from both sides; trips and searches look stops up by city.
CREATE INDEX ix_route_stops_city_id ON routes.route_stops (city_id);

-- Attach the audit columns (backstop) + the stamp/capture trigger. Idempotent.
SELECT audit.fn_enable_audit('routes.route_stops', 'route_id');

-- Traceability: the in-database pointer back to the logical model (checked by CI).
COMMENT ON TABLE routes.route_stops IS 'data-model/routes/route-stop.md';

COMMIT;
