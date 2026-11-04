-- =============================================================================
-- V0015__create_trips_schedule_class_fares.sql
-- What a seat of one seat class costs on the trips a schedule generates.
-- =============================================================================
-- Implements: data-model/trips/schedule-class-fare.md v0.1.0
-- Change tracking: audited (the default) - eight audit columns + audit.fn_enable_audit.
-- Identifier: natural key (schedule_id, seat_class) per the entity's Identity; the audit log records
--   schedule_id as the row id, so a schedule's whole fare history is one lookup.
-- fare is money: bigint minor units (paise). The entity allows whole rupees only, Rs 50 to Rs 10,000,
--   so the CHECK holds the stored value to whole hundreds of paise within that range.
-- seat_class holds the class name exactly as the bus spells it (the API writes the bus's own
--   spelling). "One fare for every seat class of the bus, and none for any other" compares against
--   another table's rows, and the bus's classes may change later, so the API enforces it.

BEGIN;

CREATE TABLE trips.schedule_class_fares (
    schedule_id      uuid        NOT NULL,
    seat_class       text        NOT NULL,
    fare             bigint      NOT NULL,

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
    CONSTRAINT pk_schedule_class_fares PRIMARY KEY (schedule_id, seat_class),
    -- Relationship: "Belongs to one Schedule."
    CONSTRAINT fk_schedule_class_fares_schedule_id FOREIGN KEY (schedule_id)
        REFERENCES trips.schedules (schedule_id),
    -- Rule: seat class names are 2-30 characters after trimming (they are stored trimmed).
    CONSTRAINT ck_schedule_class_fares_seat_class_length CHECK (
        seat_class = btrim(seat_class) AND char_length(seat_class) BETWEEN 2 AND 30),
    -- Rule: fare is INR, whole rupees, Rs 50 to Rs 10,000 (stored in paise).
    CONSTRAINT ck_schedule_class_fares_fare CHECK (fare BETWEEN 5000 AND 1000000 AND fare % 100 = 0),
    CONSTRAINT ck_schedule_class_fares_created_by_id_len CHECK (length(created_by_id) <= 64),
    CONSTRAINT ck_schedule_class_fares_updated_by_id_len CHECK (length(updated_by_id) <= 64)
);

-- Identity: "The pair (schedule, seat class name), with the name compared ignoring case."
CREATE UNIQUE INDEX uq_schedule_class_fares_schedule_id_seat_class
    ON trips.schedule_class_fares (schedule_id, lower(seat_class));

-- Attach the audit columns (backstop) + the stamp/capture trigger. Idempotent.
SELECT audit.fn_enable_audit('trips.schedule_class_fares', 'schedule_id');

-- Traceability: the in-database pointer back to the logical model (checked by CI).
COMMENT ON TABLE trips.schedule_class_fares IS 'data-model/trips/schedule-class-fare.md';

COMMIT;
