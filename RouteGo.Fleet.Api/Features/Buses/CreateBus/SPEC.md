# CAP-BUS-001 — Create bus

- Capability: CAP-BUS-001
- Canonical spec: `spec-RouteGo.Fleet.API/capabilities/buses/features/bus-management.md` (v0.1.0)
- Area: Buses
- Slice: `Features/Buses/CreateBus/`

## Contract
Implemented **verbatim** from the issued contract — if it looks wrong, report drift upstream
(`skills-spec-authoring`), never deviate locally.

- Operation: `createBus` — `contracts/buses/openapi.yaml` → `contracts/buses/bus.yaml`
- Route / verb: `POST /buses`
- Request: `BusRequest`
- Response: `201` with `Bus` and a `Location` header (`/buses/{busId}`)
- Problem codes: `VALIDATION_FAILED` (400), `UNAUTHORIZED` (401), `FORBIDDEN` (403),
  `OPERATOR_PROFILE_NOT_READY` (403), `BUS_REGISTRATION_ALREADY_EXISTS` (409)

## Acceptance criteria
Each criterion maps to tests in `CreateBus.Tests.cs`.

- [x] AC-1: Add a two-deck sleeper → `CreateBus_TwoDeckSleeper_CreatesActiveBusOwnedByTheOperator`
- [x] AC-2: Registration number already in RouteGo (add) → `CreateBus_RegistrationAlreadyInRouteGo_IsRefusedAndNothingChanges`
- [x] AC-3: Seat layout rules (all eight cases, add) → `CreateBus_LayoutBreaksARule_IsRefusedNamingTheField`
- [x] AC-4: Field rules (all six cases, add) → `CreateBus_FieldBreaksARule_IsRefusedNamingTheField`
- [x] AC-13: Profile not ready (add) → `CreateBus_OperatorWithoutProfile_IsRefusedProfileNotReady`
- [x] AC-14: Only operators (add) → `CreateBus_NotAnOperator_IsRefusedForbidden`
- [x] AC-15: Must be signed in (add) → `CreateBus_WithoutAccessToken_IsRefusedUnauthorized`

AC-5 to AC-12 belong to the list, read and replace capabilities.

## Invariants touched
- `Domain/Buses/BusRules` — registration number, name, seat class, deck, seat and layout rules
  (shared with ReplaceBus).
- Database: `uq_buses_registration_number` (registration unique across RouteGo), the seat/deck/class
  foreign keys and uniqueness constraints in `migrations/V0006`–`V0009`.

## Known deviation
- The BusSeat attribute `column` is stored as `seat_column` because COLUMN is a reserved SQL word;
  reported upstream as drift. The wire field stays `column`.
