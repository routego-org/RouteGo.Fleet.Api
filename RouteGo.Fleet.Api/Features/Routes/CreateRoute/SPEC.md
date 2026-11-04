# CAP-RTE-002 — Create route

- Capability: CAP-RTE-002
- Canonical spec: `spec-RouteGo.Fleet.API/capabilities/routes/features/route-management.md` (v0.1.0)
- Area: Routes
- Slice: `Features/Routes/CreateRoute/`

## Contract
Implemented **verbatim** from the issued contract — if it looks wrong, report drift upstream
(`skills-spec-authoring`), never deviate locally.

- Operation: `createRoute` — `contracts/routes/openapi.yaml` → `contracts/routes/route.yaml`
- Route / verb: `POST /routes`
- Request: `RouteRequest`
- Response: `201` with `Route` and a `Location` header (`/routes/{routeId}`)
- Problem codes: `VALIDATION_FAILED` (400), `UNAUTHORIZED` (401), `FORBIDDEN` (403),
  `OPERATOR_PROFILE_NOT_READY` (403), `UNKNOWN_CITY` (422, with `cityId`)

## Acceptance criteria
Each criterion maps to tests in `CreateRoute.Tests.cs`.

- [x] AC-1: Create a route with a stop → `CreateRoute_MumbaiLonavalaPune_CreatesActiveRouteOwnedByTheOperator`
- [x] AC-2: Stop rules (all cases, each distance case separately) → `CreateRoute_StopsOrDistanceBreakARule_IsRefusedNamingTheFieldAndCreatesNothing`
- [x] AC-3: A city not on the list → `CreateRoute_CityNotInTheList_IsRefusedUnknownCityCarryingItsId`
- [x] AC-11: Profile not ready (create) → `CreateRoute_OperatorWithoutProfile_IsRefusedProfileNotReady`
- [x] AC-12: Only operators, signed in (create) → `CreateRoute_NotAnOperator_IsRefusedForbidden`, `CreateRoute_WithoutAccessToken_IsRefusedUnauthorized`

AC-4 to AC-10 belong to the list, read and deactivate capabilities.

## Invariants touched
- `Domain/Routes/RouteRules` — stop count, offsets, distance.
- Database: `uq_route_stops_route_id_city_id` (no city twice), `fk_route_stops_city_id` (city from the
  list) and the per-row range checks in `migrations/V0010`–`V0011`.
- When several stops name unknown cities, `UNKNOWN_CITY` carries the first one in stop order.
