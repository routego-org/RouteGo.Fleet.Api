-- =============================================================================
-- V0007__create_buses_seat_classes.sql
-- One operator-named kind of seat on one bus.
-- =============================================================================
-- Implements: data-model/buses/seat-class.md v0.1.0
-- Change tracking: audited (the default) - eight audit columns + audit.fn_enable_audit.
-- Identifier: natural key (bus_id, name) per the entity's Identity; the audit log records bus_id
--   as the row id, so a bus's whole history is one lookup.

BEGIN;

CREATE TABLE buses.seat_classes (
    bus_id           uuid        NOT NULL,
    name             text        NOT NULL,
    display_order    smallint    NOT NULL,

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
    CONSTRAINT pk_seat_classes PRIMARY KEY (bus_id, name),
    -- Relationship: "Belongs to one Bus."
    CONSTRAINT fk_seat_classes_bus_id FOREIGN KEY (bus_id) REFERENCES buses.buses (bus_id),
    -- Rule: name is 2-30 characters after trimming (it is stored trimmed).
    CONSTRAINT ck_seat_classes_name_length CHECK (name = btrim(name) AND char_length(name) BETWEEN 2 AND 30),
    -- Rule: display order is 1-5.
    CONSTRAINT ck_seat_classes_display_order CHECK (display_order BETWEEN 1 AND 5),
    CONSTRAINT ck_seat_classes_created_by_id_len CHECK (length(created_by_id) <= 64),
    CONSTRAINT ck_seat_classes_updated_by_id_len CHECK (length(updated_by_id) <= 64)
);

-- Identity: "The pair (bus, class name), with the name compared ignoring case."
CREATE UNIQUE INDEX uq_seat_classes_bus_id_name ON buses.seat_classes (bus_id, lower(name));

-- Attach the audit columns (backstop) + the stamp/capture trigger. Idempotent.
SELECT audit.fn_enable_audit('buses.seat_classes', 'bus_id');

-- Traceability: the in-database pointer back to the logical model (checked by CI).
COMMENT ON TABLE buses.seat_classes IS 'data-model/buses/seat-class.md';

COMMIT;
