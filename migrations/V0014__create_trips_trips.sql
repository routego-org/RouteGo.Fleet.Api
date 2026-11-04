-- =============================================================================
-- V0014__create_trips_trips.sql
-- One dated, bookable departure generated from a schedule.
-- =============================================================================
-- Implements: data-model/trips/trip.md v0.1.0
-- Change tracking: audited (the default) - eight audit columns + audit.fn_enable_audit.
-- Identifier: uuid, keyed by the entity's identity attribute trip_id (the contract's tripId).
-- operator_id is text because it references operator_profiles.operator_id, an external reference.
-- created_at is both an entity attribute (when the trip was generated) and an audit column: one
--   column, stamped by the audit trigger.
-- "Trips of one bus never overlap", "only before the departure time" and "a cancelled trip blocks
--   its date" span rows or depend on the clock, so the API enforces them.

BEGIN;

CREATE TABLE trips.trips (
    trip_id            uuid        NOT NULL DEFAULT gen_random_uuid(),
    schedule_id        uuid        NOT NULL,
    operator_id        text        NOT NULL,
    route_id           uuid        NOT NULL,
    bus_id             uuid        NOT NULL,
    service_date       date        NOT NULL,
    departure_at       timestamptz NOT NULL,
    arrival_at         timestamptz NOT NULL,
    status             text        NOT NULL,
    cancellation_cause text,
    cancel_reason      text,
    cancelled_at       timestamptz,

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
    CONSTRAINT pk_trips PRIMARY KEY (trip_id),
    -- Relationship: "Generated from one Schedule."
    CONSTRAINT fk_trips_schedule_id FOREIGN KEY (schedule_id) REFERENCES trips.schedules (schedule_id),
    -- Relationship: the schedule's operator.
    CONSTRAINT fk_trips_operator_id FOREIGN KEY (operator_id)
        REFERENCES operator_profiles.operator_profiles (operator_id),
    -- Relationship: "Runs one Route."
    CONSTRAINT fk_trips_route_id FOREIGN KEY (route_id) REFERENCES routes.routes (route_id),
    -- Relationship: "Runs one Bus."
    CONSTRAINT fk_trips_bus_id FOREIGN KEY (bus_id) REFERENCES buses.buses (bus_id),
    -- Rule: arrival is the departure plus the route's journey time, so it is always later.
    CONSTRAINT ck_trips_arrival_after_departure CHECK (arrival_at > departure_at),
    -- Rule: status is one of Scheduled, Cancelled.
    CONSTRAINT ck_trips_status CHECK (status IN ('Scheduled', 'Cancelled')),
    -- Rule: cancellation cause is one of Operator, ScheduleChanged, ScheduleDeactivated.
    CONSTRAINT ck_trips_cancellation_cause CHECK (
        cancellation_cause IN ('Operator', 'ScheduleChanged', 'ScheduleDeactivated')),
    -- Rule: cancel reason is 10-500 characters.
    CONSTRAINT ck_trips_cancel_reason_length CHECK (char_length(cancel_reason) BETWEEN 10 AND 500),
    -- Rule: cause, reason and cancellation time are "set only when Cancelled" - and a cancelled
    -- trip always has all three.
    CONSTRAINT ck_trips_cancellation_cause_when_cancelled CHECK (
        (status = 'Cancelled') = (cancellation_cause IS NOT NULL)),
    CONSTRAINT ck_trips_cancel_reason_when_cancelled CHECK ((status = 'Cancelled') = (cancel_reason IS NOT NULL)),
    CONSTRAINT ck_trips_cancelled_at_when_cancelled CHECK ((status = 'Cancelled') = (cancelled_at IS NOT NULL)),
    CONSTRAINT ck_trips_created_by_id_len CHECK (length(created_by_id) <= 64),
    CONSTRAINT ck_trips_updated_by_id_len CHECK (length(updated_by_id) <= 64)
);

-- Rule: "A schedule has at most one Scheduled trip per service date."
CREATE UNIQUE INDEX uq_trips_schedule_id_service_date_scheduled
    ON trips.trips (schedule_id, service_date)
    WHERE status = 'Scheduled';

-- Every operator list and ownership check filters by operator.
CREATE INDEX ix_trips_operator_id ON trips.trips (operator_id);

-- A bus's future scheduled trips are looked up before it may be deactivated or rescheduled.
CREATE INDEX ix_trips_bus_id_departure_at_scheduled
    ON trips.trips (bus_id, departure_at)
    WHERE status = 'Scheduled';

-- Attach the audit columns (backstop) + the stamp/capture trigger. Idempotent.
SELECT audit.fn_enable_audit('trips.trips', 'trip_id');

-- Traceability: the in-database pointer back to the logical model (checked by CI).
COMMENT ON TABLE trips.trips IS 'data-model/trips/trip.md';

COMMIT;
