# CAP-RTE-004 — Get route

- Capability: CAP-RTE-004
- Canonical spec: `spec-RouteGo.Fleet.API/capabilities/routes/features/route-management.md` (v0.1.0)
- Area: Routes
- Slice: `Features/Routes/GetRoute/`

## Contract
Implemented **verbatim** from the issued contract — if it looks wrong, report drift upstream
(`skills-spec-authoring`), never deviate locally.

- Operation: `getRoute` — `contracts/routes/openapi.yaml` → `contracts/routes/route.yaml`
- Route / verb: `GET /routes/{routeId}`
- Request: path `routeId` (uuid)
- Response: `200` with `Route`
- Problem codes: `UNAUTHORIZED` (401), `FORBIDDEN` (403, caller not an operator),
  `OPERATOR_PROFILE_NOT_READY` (403), `NOT_FOUND` (404)

## Acceptance criteria
Each criterion maps to tests in `GetRoute.Tests.cs`.

- [x] AC-6: Read one route → `GetRoute_OwnRouteMumbaiLonavalaPune_ReturnsEveryStopInOrder`
- [x] AC-9: Unknown route (read) → `GetRoute_UnknownRoute_IsRefusedNotFound`
- [~] AC-10: Another operator's route (read) → `GetRoute_AnotherOperatorsRoute_IsReportedNotFound` —
  **deliberately 404, not the spec's FORBIDDEN** (see Known deviation)
- [x] AC-11: Profile not ready (read) → `GetRoute_OperatorWithoutProfile_IsRefusedProfileNotReady`
- [x] AC-12: Only operators, signed in (read) → `GetRoute_NotAnOperator_IsRefusedForbidden`, `GetRoute_WithoutAccessToken_IsRefusedUnauthorized`

## Invariants touched
- None in `Domain/`; ownership is in the lookup's `WHERE` clause.

## Known deviation
- Another operator's route answers `404 NOT_FOUND`, indistinguishable from a missing route, per
  the RouteGo coding standard's rule that out-of-scope resources return 404, never 403. Decided by
  the developer on 2026-10-09; drift reported upstream against AC-10 and the contract's 403 description.
- A `routeId` that is not a UUID answers `400 VALIDATION_FAILED` (the contract lists no 400 for this
  operation; model binding refuses the value before the slice runs).
