-- =============================================================================
-- V0010__create_routes_routes.sql
-- An operator's path from a source city to a destination city.
-- =============================================================================
-- Implements: data-model/routes/route.md v0.1.0
-- Change tracking: audited (the default) - eight audit columns + audit.fn_enable_audit.
-- Identifier: uuid, keyed by the entity's identity attribute route_id (the contract's routeId).
-- operator_id is text because it references operator_profiles.operator_id, an external reference.
-- created_at is both an entity attribute and an audit column: one column, stamped by the trigger.
-- Source, destination and journey time are derived from routes.route_stops and not stored (entity file).

BEGIN;

CREATE TABLE routes.routes (
    route_id         uuid        NOT NULL DEFAULT gen_random_uuid(),
    operator_id      text        NOT NULL,
    distance_km      smallint    NOT NULL,
    status           text        NOT NULL,
    deactivated_at   timestamptz,

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
    CONSTRAINT pk_routes PRIMARY KEY (route_id),
    -- Relationship: "Belongs to one OperatorProfile."
    CONSTRAINT fk_routes_operator_id FOREIGN KEY (operator_id)
        REFERENCES operator_profiles.operator_profiles (operator_id),
    -- Rule: distance is whole kilometres, 1-3,000.
    CONSTRAINT ck_routes_distance_km CHECK (distance_km BETWEEN 1 AND 3000),
    -- Rule: status is one of Active, Inactive.
    CONSTRAINT ck_routes_status CHECK (status IN ('Active', 'Inactive')),
    CONSTRAINT ck_routes_created_by_id_len CHECK (length(created_by_id) <= 64),
    CONSTRAINT ck_routes_updated_by_id_len CHECK (length(updated_by_id) <= 64)
);

-- Every operator list and ownership check filters by operator.
CREATE INDEX ix_routes_operator_id ON routes.routes (operator_id);

-- Attach the audit columns (backstop) + the stamp/capture trigger. Idempotent.
SELECT audit.fn_enable_audit('routes.routes', 'route_id');

-- Traceability: the in-database pointer back to the logical model (checked by CI).
COMMENT ON TABLE routes.routes IS 'data-model/routes/route.md';

COMMIT;
