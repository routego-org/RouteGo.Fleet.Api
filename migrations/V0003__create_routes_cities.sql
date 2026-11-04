-- =============================================================================
-- V0003__create_routes_cities.sql
-- The one list of cities a route may start at, stop at or end at.
-- =============================================================================
-- Implements: data-model/routes/city.md v0.1.0
-- Change tracking: EXCLUDED (references/audit.md, "Excluding a table from the audit").
--   Reason: Static list seeded by migrations; never changed through the product, and cities are never removed.
-- Identifier: uuid, keyed by the entity's identity attribute city_id (the contract's cityId).
--   The seed (next migration) fixes every value, so a city has the same id in every environment.

BEGIN;

CREATE SCHEMA IF NOT EXISTS routes;

CREATE TABLE routes.cities (
    city_id          uuid        NOT NULL DEFAULT gen_random_uuid(),
    name             text        NOT NULL,
    state            text        NOT NULL,

    -- No audit columns: this table is declared excluded below. fn_exclude_from_audit
    -- refuses a table that carries any of them - audited or excluded, never half.
    CONSTRAINT pk_cities PRIMARY KEY (city_id),
    -- Rule: "No two cities share a name within the same state."
    CONSTRAINT uq_cities_state_name UNIQUE (state, name)
);

-- Declare the exclusion, with its reason, so the CI audit gate and the checkers PASS for
-- this table and the database itself says why it has no history. Idempotent.
SELECT audit.fn_exclude_from_audit('routes.cities', 'Static list seeded by migrations; never changed through the product, and cities are never removed.');

-- Traceability: the in-database pointer back to the logical model (checked by CI).
COMMENT ON TABLE routes.cities IS 'data-model/routes/city.md';

COMMIT;
