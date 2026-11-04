-- =============================================================================
-- V0013__create_trips_schedules.sql
-- An operator's standing timetable: one bus on one route, departing at a time of day on chosen days.
-- =============================================================================
-- Implements: data-model/trips/schedule.md v0.1.0
-- Change tracking: audited (the default) - eight audit columns + audit.fn_enable_audit.
-- Identifier: uuid, keyed by the entity's identity attribute schedule_id (the contract's scheduleId).
-- operator_id is text because it references operator_profiles.operator_id, an external reference.
-- days_of_week is a set of enumeration values, kept as a text[] whose CHECK allows only the listed
--   values; "different days" is enforced by the API, which never writes a duplicate.
-- trips_generated_through is a date but keeps the entity's attribute name verbatim rather than the
--   *_on / *_date convention, so the column, the entity file and the contract all read the same.
-- created_at / updated_at are both entity attributes and audit columns: one column each,
--   stamped by the audit trigger.
-- "The route and bus belong to the schedule's operator" and "the bus and route are active" span
--   rows of other tables, so the API enforces them.

BEGIN;

CREATE SCHEMA IF NOT EXISTS trips;

CREATE TABLE trips.schedules (
    schedule_id             uuid        NOT NULL DEFAULT gen_random_uuid(),
    operator_id             text        NOT NULL,
    route_id                uuid        NOT NULL,
    bus_id                  uuid        NOT NULL,
    departure_time          text        NOT NULL,
    days_of_week            text[]      NOT NULL,
    status                  text        NOT NULL,
    trips_generated_through date        NOT NULL,

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
    CONSTRAINT pk_schedules PRIMARY KEY (schedule_id),
    -- Relationship: "Belongs to one OperatorProfile."
    CONSTRAINT fk_schedules_operator_id FOREIGN KEY (operator_id)
        REFERENCES operator_profiles.operator_profiles (operator_id),
    -- Relationship: "Uses one Route."
    CONSTRAINT fk_schedules_route_id FOREIGN KEY (route_id) REFERENCES routes.routes (route_id),
    -- Relationship: "Uses one Bus."
    CONSTRAINT fk_schedules_bus_id FOREIGN KEY (bus_id) REFERENCES buses.buses (bus_id),
    -- Rule: departure time is HH:mm on the 24-hour clock.
    CONSTRAINT ck_schedules_departure_time CHECK (departure_time ~ '^([01][0-9]|2[0-3]):[0-5][0-9]$'),
    -- Rule: days of week are drawn only from Monday-Sunday, a set of 1 to 7 days.
    CONSTRAINT ck_schedules_days_of_week CHECK (days_of_week <@ ARRAY[
        'Monday', 'Tuesday', 'Wednesday', 'Thursday', 'Friday', 'Saturday', 'Sunday']::text[]),
    CONSTRAINT ck_schedules_days_of_week_count CHECK (cardinality(days_of_week) BETWEEN 1 AND 7),
    -- Rule: status is one of Active, Inactive.
    CONSTRAINT ck_schedules_status CHECK (status IN ('Active', 'Inactive')),
    CONSTRAINT ck_schedules_created_by_id_len CHECK (length(created_by_id) <= 64),
    CONSTRAINT ck_schedules_updated_by_id_len CHECK (length(updated_by_id) <= 64)
);

-- Every operator list and ownership check filters by operator.
CREATE INDEX ix_schedules_operator_id ON trips.schedules (operator_id);

-- Attach the audit columns (backstop) + the stamp/capture trigger. Idempotent.
SELECT audit.fn_enable_audit('trips.schedules', 'schedule_id');

-- Traceability: the in-database pointer back to the logical model (checked by CI).
COMMENT ON TABLE trips.schedules IS 'data-model/trips/schedule.md';

COMMIT;
