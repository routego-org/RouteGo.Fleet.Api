-- =============================================================================
-- V0012__create_buses_bus_photos.sql
-- One photo of a bus, from its pending upload slot to the confirmed file.
-- =============================================================================
-- Implements: data-model/buses/bus-photo.md v0.1.0
-- Change tracking: audited (the default) - eight audit columns + audit.fn_enable_audit.
-- Identifier: uuid, keyed by the entity's identity attribute photo_id (the contract's photoId).
-- created_at is both an entity attribute (when the slot was issued) and an audit column: one
--   column, stamped by the audit trigger.
-- size_bytes has no upper bound here: the maximum is a configured setting (3 MB today), so the API
--   enforces it; a CHECK would need a migration every time the setting changes.
-- "At most 5 photos per bus" counts unexpired pending slots, which depends on the clock, so the
--   API enforces it.

BEGIN;

CREATE TABLE buses.bus_photos (
    photo_id         uuid        NOT NULL DEFAULT gen_random_uuid(),
    bus_id           uuid        NOT NULL,
    file_location    text        NOT NULL,
    format           text        NOT NULL,
    size_bytes       integer     NOT NULL,
    status           text        NOT NULL,
    position         smallint,
    slot_expires_at  timestamptz NOT NULL,
    uploaded_at      timestamptz,

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
    CONSTRAINT pk_bus_photos PRIMARY KEY (photo_id),
    -- Relationship: "Belongs to one Bus."
    CONSTRAINT fk_bus_photos_bus_id FOREIGN KEY (bus_id) REFERENCES buses.buses (bus_id),
    -- Rule: format is one of Jpeg, Png.
    CONSTRAINT ck_bus_photos_format CHECK (format IN ('Jpeg', 'Png')),
    -- Rule: size is at least 1 byte.
    CONSTRAINT ck_bus_photos_size_bytes CHECK (size_bytes >= 1),
    -- Rule: status is one of Pending, Uploaded.
    CONSTRAINT ck_bus_photos_status CHECK (status IN ('Pending', 'Uploaded')),
    -- Rule: position is 1-5, "set only when Uploaded" - and every uploaded photo has one, since
    -- the uploaded photos are numbered 1, 2, 3 ... without gaps.
    CONSTRAINT ck_bus_photos_position CHECK (position BETWEEN 1 AND 5),
    CONSTRAINT ck_bus_photos_position_when_uploaded CHECK ((status = 'Uploaded') = (position IS NOT NULL)),
    -- Rule: uploaded_at is "when the upload was confirmed", so only an uploaded photo has one.
    CONSTRAINT ck_bus_photos_uploaded_at_when_uploaded CHECK ((status = 'Uploaded') = (uploaded_at IS NOT NULL)),
    -- Rule: no two uploaded photos of a bus share a place in its photo order. Deferred so a
    -- reorder can move every position inside one transaction.
    CONSTRAINT uq_bus_photos_bus_id_position UNIQUE (bus_id, position) DEFERRABLE INITIALLY DEFERRED,
    CONSTRAINT ck_bus_photos_created_by_id_len CHECK (length(created_by_id) <= 64),
    CONSTRAINT ck_bus_photos_updated_by_id_len CHECK (length(updated_by_id) <= 64)
);

-- Attach the audit columns (backstop) + the stamp/capture trigger. Idempotent.
SELECT audit.fn_enable_audit('buses.bus_photos', 'photo_id');

-- Traceability: the in-database pointer back to the logical model (checked by CI).
COMMENT ON TABLE buses.bus_photos IS 'data-model/buses/bus-photo.md';

COMMIT;
