-- =============================================================================
-- V0016__create_trips_trip_class_fares.sql
-- What a seat of one seat class costs on one trip.
-- =============================================================================
-- Implements: data-model/trips/trip-class-fare.md v0.1.0
-- Change tracking: audited (the default) - eight audit columns + audit.fn_enable_audit.
-- Identifier: natural key (trip_id, seat_class) per the entity's Identity; the audit log records
--   trip_id as the row id, so a trip's whole fare history is one lookup.
-- fare is money: bigint minor units (paise). The entity allows whole rupees only, Rs 50 to Rs 10,000,
--   so the CHECK holds the stored value to whole hundreds of paise within that range.
-- seat_class holds the class name exactly as the trip's bus spells it (the API writes the bus's own
--   spelling). "One fare for every seat class of the bus" and "changes only more than 6 hours before
--   departure" compare against other rows and the clock, so the API enforces them.

BEGIN;

CREATE TABLE trips.trip_class_fares (
    trip_id          uuid        NOT NULL,
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
    CONSTRAINT pk_trip_class_fares PRIMARY KEY (trip_id, seat_class),
    -- Relationship: "Belongs to one Trip."
    CONSTRAINT fk_trip_class_fares_trip_id FOREIGN KEY (trip_id) REFERENCES trips.trips (trip_id),
    -- Rule: seat class names are 2-30 characters after trimming (they are stored trimmed).
    CONSTRAINT ck_trip_class_fares_seat_class_length CHECK (
        seat_class = btrim(seat_class) AND char_length(seat_class) BETWEEN 2 AND 30),
    -- Rule: fare is INR, whole rupees, Rs 50 to Rs 10,000 (stored in paise).
    CONSTRAINT ck_trip_class_fares_fare CHECK (fare BETWEEN 5000 AND 1000000 AND fare % 100 = 0),
    CONSTRAINT ck_trip_class_fares_created_by_id_len CHECK (length(created_by_id) <= 64),
    CONSTRAINT ck_trip_class_fares_updated_by_id_len CHECK (length(updated_by_id) <= 64)
);

-- Identity: "The pair (trip, seat class name), with the name compared ignoring case."
CREATE UNIQUE INDEX uq_trip_class_fares_trip_id_seat_class
    ON trips.trip_class_fares (trip_id, lower(seat_class));

-- Attach the audit columns (backstop) + the stamp/capture trigger. Idempotent.
SELECT audit.fn_enable_audit('trips.trip_class_fares', 'trip_id');

-- Traceability: the in-database pointer back to the logical model (checked by CI).
COMMENT ON TABLE trips.trip_class_fares IS 'data-model/trips/trip-class-fare.md';

COMMIT;
