-- =============================================================================
-- V0006__create_buses_buses.sql
-- One vehicle an operator runs.
-- =============================================================================
-- Implements: data-model/buses/bus.md v0.1.0
-- Change tracking: audited (the default) - eight audit columns + audit.fn_enable_audit.
-- Identifier: uuid, keyed by the entity's identity attribute bus_id (the contract's busId).
-- operator_id is text because it references operator_profiles.operator_id, an external reference.
-- amenities is a set of enumeration values, kept as a text[] whose CHECK allows only the listed
--   values; "each at most once" is enforced by the API, which never writes a duplicate.
-- created_at / updated_at are both entity attributes and audit columns: one column each,
--   stamped by the audit trigger.
-- The total seat count is derived from buses.bus_seats and not stored (entity file).

BEGIN;

CREATE SCHEMA IF NOT EXISTS buses;

CREATE TABLE buses.buses (
    bus_id              uuid        NOT NULL DEFAULT gen_random_uuid(),
    operator_id         text        NOT NULL,
    registration_number text        NOT NULL,
    name                text        NOT NULL,
    bus_type            text        NOT NULL,
    is_ac               boolean     NOT NULL,
    amenities           text[],
    status              text        NOT NULL,

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
    CONSTRAINT pk_buses PRIMARY KEY (bus_id),
    -- Relationship: "Belongs to one OperatorProfile."
    CONSTRAINT fk_buses_operator_id FOREIGN KEY (operator_id)
        REFERENCES operator_profiles.operator_profiles (operator_id),
    -- Rule: registration number is letters and digits only, upper case, spaces and hyphens
    -- removed, 6-15 characters.
    CONSTRAINT ck_buses_registration_number_format CHECK (registration_number ~ '^[A-Z0-9]{6,15}$'),
    -- Rule: name is 2-80 characters.
    CONSTRAINT ck_buses_name_length CHECK (char_length(name) BETWEEN 2 AND 80),
    -- Rule: bus type is one of Seater, Sleeper, SemiSleeper.
    CONSTRAINT ck_buses_bus_type CHECK (bus_type IN ('Seater', 'Sleeper', 'SemiSleeper')),
    -- Rule: amenities are drawn only from the fixed list.
    CONSTRAINT ck_buses_amenities CHECK (amenities <@ ARRAY[
        'Wifi', 'ChargingPoint', 'Blanket', 'WaterBottle', 'ReadingLight', 'Cctv', 'GpsTracking', 'Restroom']::text[]),
    -- Rule: status is one of Active, Inactive.
    CONSTRAINT ck_buses_status CHECK (status IN ('Active', 'Inactive')),
    CONSTRAINT ck_buses_created_by_id_len CHECK (length(created_by_id) <= 64),
    CONSTRAINT ck_buses_updated_by_id_len CHECK (length(updated_by_id) <= 64)
);

-- Rule: "The registration number is unique across all buses of all operators, active or
-- inactive" - stored already normalized, so a plain unique constraint compares it correctly.
CREATE UNIQUE INDEX uq_buses_registration_number ON buses.buses (registration_number);

-- Every operator list and ownership check filters by operator.
CREATE INDEX ix_buses_operator_id ON buses.buses (operator_id);

-- Attach the audit columns (backstop) + the stamp/capture trigger. Idempotent.
SELECT audit.fn_enable_audit('buses.buses', 'bus_id');

-- Traceability: the in-database pointer back to the logical model (checked by CI).
COMMENT ON TABLE buses.buses IS 'data-model/buses/bus.md';

COMMIT;
