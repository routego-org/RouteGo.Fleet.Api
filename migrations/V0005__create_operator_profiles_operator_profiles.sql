-- =============================================================================
-- V0005__create_operator_profiles_operator_profiles.sql
-- Fleet's record of a bus company an admin approved in Identity.
-- =============================================================================
-- Implements: data-model/operator-profiles/operator-profile.md v0.1.0
-- Change tracking: audited (the default) - eight audit columns + audit.fn_enable_audit.
-- Identifier: keyed by the entity's identity attribute operator_id, an external reference to
--   Identity's account id (the access token's operatorId claim) - text, never validated here.
-- Rows are written by the OperatorApproved worker (a separate codebase); this API only reads them.
-- created_at is both an entity attribute and an audit column: one column, stamped by the trigger.

BEGIN;

CREATE SCHEMA IF NOT EXISTS operator_profiles;

CREATE TABLE operator_profiles.operator_profiles (
    operator_id      text         NOT NULL,
    company_name     text         NOT NULL,
    email            text         NOT NULL,
    rating           numeric(2,1) NOT NULL,
    approved_at      timestamptz  NOT NULL,

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
    -- Rule: "At most one profile per operator identifier."
    CONSTRAINT pk_operator_profiles PRIMARY KEY (operator_id),
    -- Rule: company name is 2-150 characters (per Identity).
    CONSTRAINT ck_operator_profiles_company_name_length CHECK (char_length(company_name) BETWEEN 2 AND 150),
    -- Rule: rating is 0.0 to 5.0, one decimal place (the numeric(2,1) scale holds the decimal).
    CONSTRAINT ck_operator_profiles_rating CHECK (rating BETWEEN 0.0 AND 5.0),
    CONSTRAINT ck_operator_profiles_created_by_id_len CHECK (length(created_by_id) <= 64),
    CONSTRAINT ck_operator_profiles_updated_by_id_len CHECK (length(updated_by_id) <= 64)
);

-- Attach the audit columns (backstop) + the stamp/capture trigger. Idempotent.
SELECT audit.fn_enable_audit('operator_profiles.operator_profiles', 'operator_id');

-- Traceability: the in-database pointer back to the logical model (checked by CI).
COMMENT ON TABLE operator_profiles.operator_profiles IS 'data-model/operator-profiles/operator-profile.md';

COMMIT;
