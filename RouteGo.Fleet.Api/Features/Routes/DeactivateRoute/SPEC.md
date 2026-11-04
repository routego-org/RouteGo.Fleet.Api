# CAP-RTE-005 — Deactivate route

- Capability: CAP-RTE-005
- Canonical spec: `spec-RouteGo.Fleet.API/capabilities/routes/features/route-management.md` (v0.1.0)
- Area: Routes
- Slice: `Features/Routes/DeactivateRoute/`

## Contract
Implemented **verbatim** from the issued contract — if it looks wrong, report drift upstream
(`skills-spec-authoring`), never deviate locally.

- Operation: `deactivateRoute` — `contracts/routes/openapi.yaml` → `contracts/routes/route.yaml`
- Route / verb: `POST /routes/{routeId}/deactivate`
- Request: path `routeId` (uuid); no body
- Response: `200` with `Route`
- Problem codes: `UNAUTHORIZED` (401), `FORBIDDEN` (403, caller not an operator),
  `OPERATOR_PROFILE_NOT_READY` (403), `NOT_FOUND` (404)

## Acceptance criteria
Each criterion maps to tests in `DeactivateRoute.Tests.cs`.

- [x] AC-7: Deactivate a route → `DeactivateRoute_ActiveRoute_BecomesInactiveNow` covers the route
  becoming inactive with `deactivatedAt` from the clock. The handler writes `routes.routes` only, so
  schedules and trips cannot change. The "new schedule on R1 is refused with ROUTE_INACTIVE" clause
  is tested by CreateSchedule (CAP-TRP-001), which owns that refusal:
  `CreateSchedule_InactiveRoute_IsRefusedRouteInactive`.
- [x] AC-8: Deactivating twice changes nothing → `DeactivateRoute_AlreadyInactive_ReturnsRouteUnchanged`
- [x] AC-9: Unknown route (deactivate) → `DeactivateRoute_UnknownRoute_IsRefusedNotFound`
- [~] AC-10: Another operator's route (deactivate) → `DeactivateRoute_AnotherOperatorsRoute_IsReportedNotFoundAndLeftActive` —
  **deliberately 404, not the spec's FORBIDDEN** (see Known deviation)
- [x] AC-11: Profile not ready (deactivate) → `DeactivateRoute_OperatorWithoutProfile_IsRefusedProfileNotReady`
- [x] AC-12: Only operators, signed in (deactivate) → `DeactivateRoute_NotAnOperator_IsRefusedForbidden`, `DeactivateRoute_WithoutAccessToken_IsRefusedUnauthorized`

## Invariants touched
- None in `Domain/`. Ownership and "only an active route changes" are in the UPDATE's `WHERE`
  clause, so a repeat deactivation keeps the first `deactivatedAt` and writes no audit entry.

## Known deviation
- Another operator's route answers `404 NOT_FOUND`, indistinguishable from a missing route, per
  the RouteGo coding standard's rule that out-of-scope resources return 404, never 403. Decided by
  the developer on 2026-10-09; drift reported upstream against AC-10 and the contract's 403 description.
- A `routeId` that is not a UUID answers `400 VALIDATION_FAILED` (the contract lists no 400 for this
  operation; model binding refuses the value before the slice runs).
