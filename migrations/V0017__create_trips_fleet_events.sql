-- =============================================================================
-- V0017__create_trips_fleet_events.sql
-- One event Fleet tells the rest of RouteGo about, recorded with the trip change that caused it.
-- =============================================================================
-- Implements: data-model/trips/fleet-event.md v0.1.0
-- Change tracking: EXCLUDED - see the reason in the fn_exclude_from_audit call below.
-- Identifier: uuid, keyed by the entity's identity attribute event_id (the envelope's eventId). The
--   API assigns it when it records the event.
-- data is the entity's text attribute holding the event data as JSON; it is jsonb so the database
--   refuses malformed JSON and the publisher can read fields without parsing text.
-- transaction_id is physical only: the writing transaction's id. Events are published in commit
--   order, which neither recorded_at nor event_id gives; the publisher reads only rows whose
--   transaction is older than every still-running one (pg_snapshot_xmin) and orders by this column.
-- Rows are inserted by this API and stamped delivered_at / delay_alerted_at by the separate
--   publisher (CAP-TRP-012); neither writer changes anything else.

BEGIN;

CREATE TABLE trips.fleet_events (
    event_id         uuid        NOT NULL,
    event_type       text        NOT NULL,
    trip_id          uuid        NOT NULL,
    data             jsonb       NOT NULL,
    correlation_id   text        NOT NULL,
    recorded_at      timestamptz NOT NULL,
    delivered_at     timestamptz,
    delay_alerted_at timestamptz,
    transaction_id   xid8        NOT NULL DEFAULT pg_current_xact_id(),
    CONSTRAINT pk_fleet_events PRIMARY KEY (event_id),
    -- Relationship: "Describes one Trip."
    CONSTRAINT fk_fleet_events_trip_id FOREIGN KEY (trip_id) REFERENCES trips.trips (trip_id),
    -- Rule: event type is one of TripScheduled, FareChanged, TripCancelled.
    CONSTRAINT ck_fleet_events_event_type CHECK (event_type IN ('TripScheduled', 'FareChanged', 'TripCancelled')),
    -- Rule: the correlation id is copied from the request, which accepts at most 64 characters.
    CONSTRAINT ck_fleet_events_correlation_id_length CHECK (char_length(correlation_id) BETWEEN 1 AND 64)
);

-- The publisher's scan: undelivered events in commit order.
CREATE INDEX ix_fleet_events_transaction_id_undelivered
    ON trips.fleet_events (transaction_id, recorded_at)
    WHERE delivered_at IS NULL;

-- The trip foreign key is checked from both sides; a trip's events are looked up by trip.
CREATE INDEX ix_fleet_events_trip_id ON trips.fleet_events (trip_id);

SELECT audit.fn_exclude_from_audit('trips.fleet_events',
    'Outbox of trip events: rows are inserted with the audited trip change that caused them and afterwards only stamped as delivered or alerted by the publisher.');

-- Traceability: the in-database pointer back to the logical model (checked by CI).
COMMENT ON TABLE trips.fleet_events IS 'data-model/trips/fleet-event.md';

COMMIT;
