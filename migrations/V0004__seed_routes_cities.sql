-- =============================================================================
-- V0004__seed_routes_cities.sql
-- The version 1 city list: 20 cities in Maharashtra and its neighbouring states.
-- =============================================================================
-- Implements: data-model/routes/city.md v0.1.0, rule "The seeded list is the 20 cities in
--   capabilities/routes/features/city-list.md" (business rule 3 there).
-- The ids are fixed rather than generated so a city has the same cityId in every environment:
-- routes, schedules and published trip events all carry it.
-- routes.cities is excluded from the audit, so no actor or action needs stamping here.

BEGIN;

INSERT INTO routes.cities (city_id, name, state) VALUES
    ('4e792207-c455-49f7-89d5-bdd56eb46fa9', 'Ahmedabad',  'Gujarat'),
    ('ba3b9ccd-316c-4506-be34-930461989820', 'Aurangabad', 'Maharashtra'),
    ('b85567d2-ef6f-412e-acad-f2d7865537cd', 'Belagavi',   'Karnataka'),
    ('d71b7577-bfbd-4e33-b2c9-9b2a5cba16fe', 'Bengaluru',  'Karnataka'),
    ('06220551-2a5d-484b-8e96-4e86963cea0f', 'Hubballi',   'Karnataka'),
    ('bcb45c60-27fc-4f0b-8f01-60ab6717681a', 'Hyderabad',  'Telangana'),
    ('8892a678-627b-4e9b-84bf-00790132e903', 'Indore',     'Madhya Pradesh'),
    ('0256b37a-944f-45ab-8954-3521d54efad5', 'Kolhapur',   'Maharashtra'),
    ('ef669cf3-be1f-491d-9c86-7395ecb67f75', 'Lonavala',   'Maharashtra'),
    ('9822ce8c-2354-4913-9b17-854ea1f6aca8', 'Mangaluru',  'Karnataka'),
    ('ffe095c5-e8b5-4c45-93f9-b0d0aa8d6a37', 'Mumbai',     'Maharashtra'),
    ('ece48a00-91a9-42d9-b3c5-a6d81b05b55d', 'Nagpur',     'Maharashtra'),
    ('56e2c262-b688-4bab-81bb-515b1bb8af35', 'Nashik',     'Maharashtra'),
    ('ffcacdfe-153a-4a20-9606-e04c12f02994', 'Panaji',     'Goa'),
    ('0484575c-a368-4057-896d-c99d78ba9cce', 'Pune',       'Maharashtra'),
    ('32159710-7956-442c-8d4a-8a67211fa1d5', 'Satara',     'Maharashtra'),
    ('66544536-7ba5-4c14-b513-ee9fea03c26d', 'Shirdi',     'Maharashtra'),
    ('bda92852-ac69-4c75-84df-d3a1881e3ea7', 'Solapur',    'Maharashtra'),
    ('73a80ff7-c2c1-4076-bb93-7e455e8e9cd9', 'Surat',      'Gujarat'),
    ('50e5040c-7ffb-48f9-b5ec-0dea816bdda4', 'Vadodara',   'Gujarat');

COMMIT;
