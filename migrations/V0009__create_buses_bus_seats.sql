-- =============================================================================
-- V0009__create_buses_bus_seats.sql
-- One bookable seat on a bus.
-- =============================================================================
-- Implements: data-model/buses/bus-seat.md v0.1.0
-- Change tracking: audited (the default) - eight audit columns + audit.fn_enable_audit.
-- Identifier: natural key (bus_id, label) per the entity's Identity; the audit log records bus_id
--   as the row id, so a bus's whole history is one lookup.
-- seat_column holds the entity's attribute `column`, renamed because COLUMN is a reserved SQL word
--   (reported upstream as drift; the attribute name is the spec's to change).
-- seat_class holds the class name exactly as stored in buses.seat_classes (the API writes the
--   class's own spelling), so it can reference that row directly.
-- "Row and column inside the deck's grid" compares against another table's values, so the API
--   enforces it; the database enforces everything stated per row.

BEGIN;

CREATE TABLE buses.bus_seats (
    bus_id           uuid        NOT NULL,
    label            text        NOT NULL,
    deck             text        NOT NULL,
    row              smallint    NOT NULL,
    seat_column      smallint    NOT NULL,
    seat_class       text        NOT NULL,

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
    CONSTRAINT pk_bus_seats PRIMARY KEY (bus_id, label),
    -- Relationship: "Belongs to one Bus."
    CONSTRAINT fk_bus_seats_bus_id FOREIGN KEY (bus_id) REFERENCES buses.buses (bus_id),
    -- Relationship: "sits on one BusDeck" - the deck must be one of the bus's decks.
    CONSTRAINT fk_bus_seats_bus_id_deck FOREIGN KEY (bus_id, deck) REFERENCES buses.bus_decks (bus_id, deck),
    -- Relationship: "belongs to one SeatClass" of the same bus.
    CONSTRAINT fk_bus_seats_bus_id_seat_class FOREIGN KEY (bus_id, seat_class)
        REFERENCES buses.seat_classes (bus_id, name),
    -- Rule: label is 1-5 letters or digits.
    CONSTRAINT ck_bus_seats_label_format CHECK (label ~ '^[A-Za-z0-9]{1,5}$'),
    -- Rule: deck is one of Lower, Upper.
    CONSTRAINT ck_bus_seats_deck CHECK (deck IN ('Lower', 'Upper')),
    -- Rule: row and column are whole numbers within the largest grid a deck may have.
    CONSTRAINT ck_bus_seats_row CHECK (row BETWEEN 1 AND 15),
    CONSTRAINT ck_bus_seats_column CHECK (seat_column BETWEEN 1 AND 6),
    -- Rule: "No two seats of a bus share ... the same deck, row and column."
    CONSTRAINT uq_bus_seats_bus_id_deck_row_column UNIQUE (bus_id, deck, row, seat_column),
    CONSTRAINT ck_bus_seats_created_by_id_len CHECK (length(created_by_id) <= 64),
    CONSTRAINT ck_bus_seats_updated_by_id_len CHECK (length(updated_by_id) <= 64)
);

-- Identity: "The pair (bus, seat label), with the label compared ignoring case."
CREATE UNIQUE INDEX uq_bus_seats_bus_id_label ON buses.bus_seats (bus_id, lower(label));

-- The seat-class foreign key is checked on every seat-class change.
CREATE INDEX ix_bus_seats_bus_id_seat_class ON buses.bus_seats (bus_id, seat_class);

-- Attach the audit columns (backstop) + the stamp/capture trigger. Idempotent.
SELECT audit.fn_enable_audit('buses.bus_seats', 'bus_id');

-- Traceability: the in-database pointer back to the logical model (checked by CI).
COMMENT ON TABLE buses.bus_seats IS 'data-model/buses/bus-seat.md';

COMMIT;
