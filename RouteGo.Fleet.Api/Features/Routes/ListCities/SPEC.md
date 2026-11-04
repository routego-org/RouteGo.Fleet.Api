# CAP-RTE-001 — List cities

- Capability: CAP-RTE-001
- Canonical spec: `spec-RouteGo.Fleet.API/capabilities/routes/features/city-list.md` (v0.1.0)
- Area: Routes
- Slice: `Features/Routes/ListCities/`

## Contract
Implemented **verbatim** from the issued contract — if it looks wrong, report drift upstream
(`skills-spec-authoring`), never deviate locally.

- Operation: `listCities` — `contracts/routes/openapi.yaml` → `contracts/routes/city.yaml`
- Route / verb: `GET /cities`
- Request: none (no parameters, no body)
- Response: `200` with `CityList` (`items[]` of `City`: `cityId`, `name`, `state`)
- Problem codes: `UNAUTHORIZED` (401) — written by the bearer handler (`Common/Security/AuthenticationProblems`)

## Acceptance criteria
Each criterion maps to one test in `ListCities.Tests.cs`.

- [x] AC-1: Read the list → `ListCities_AnySignedInRole_ReturnsAllSeededCitiesSortedByName` (Operator, Admin, Customer)
- [x] AC-2: An operator without a profile can read it → `ListCities_OperatorWithoutProfile_ReturnsTheList`
- [x] AC-3: Must be signed in → `ListCities_WithoutAccessToken_IsRefusedUnauthorized`, `ListCities_TokenSignedByUntrustedKey_IsRefusedUnauthorized`

## Invariants touched
Business rules live in `Domain/`, never in this slice. List the ones this feature relies on.

- None in `Domain/`. The city list is seeded by `migrations/V0004__seed_routes_cities.sql`; "name unique
  within its state" is the `uq_cities_state_name` constraint (`V0003__create_routes_cities.sql`).
