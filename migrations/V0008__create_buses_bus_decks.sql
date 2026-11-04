-- =============================================================================
-- V0008__create_buses_bus_decks.sql
-- One level of a bus and its grid of rows and columns.
-- =============================================================================
-- Implements: data-model/buses/bus-deck.md v0.1.0
-- Change tracking: audited (the default) - eight audit columns + audit.fn_enable_audit.
-- Identifier: natural key (bus_id, deck) per the entity's Identity; the audit log records bus_id
--   as the row id, so a bus's whole history is one lookup.

BEGIN;

CREATE TABLE buses.bus_decks (
    bus_id             uuid        NOT NULL,
    deck               text        NOT NULL,
    rows               smallint    NOT NULL,
    columns            smallint    NOT NULL,
    aisle_after_column smallint,

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
    CONSTRAINT pk_bus_decks PRIMARY KEY (bus_id, deck),
    -- Relationship: "Belongs to one Bus."
    CONSTRAINT fk_bus_decks_bus_id FOREIGN KEY (bus_id) REFERENCES buses.buses (bus_id),
    -- Rule: deck is one of Lower, Upper.
    CONSTRAINT ck_bus_decks_deck CHECK (deck IN ('Lower', 'Upper')),
    -- Rule: rows is a whole number 1-15.
    CONSTRAINT ck_bus_decks_rows CHECK (rows BETWEEN 1 AND 15),
    -- Rule: columns is a whole number 1-6.
    CONSTRAINT ck_bus_decks_columns CHECK (columns BETWEEN 1 AND 6),
    -- Rule: the aisle runs after a column from 1 to one less than columns.
    CONSTRAINT ck_bus_decks_aisle_after_column CHECK (aisle_after_column BETWEEN 1 AND columns - 1),
    CONSTRAINT ck_bus_decks_created_by_id_len CHECK (length(created_by_id) <= 64),
    CONSTRAINT ck_bus_decks_updated_by_id_len CHECK (length(updated_by_id) <= 64)
);

-- Attach the audit columns (backstop) + the stamp/capture trigger. Idempotent.
SELECT audit.fn_enable_audit('buses.bus_decks', 'bus_id');

-- Traceability: the in-database pointer back to the logical model (checked by CI).
COMMENT ON TABLE buses.bus_decks IS 'data-model/buses/bus-deck.md';

COMMIT;
