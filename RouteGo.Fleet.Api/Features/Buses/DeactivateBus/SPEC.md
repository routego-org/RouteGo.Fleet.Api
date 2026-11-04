# CAP-BUS-005 — Deactivate bus

- Capability: CAP-BUS-005
- Canonical spec: `spec-RouteGo.Fleet.API/capabilities/buses/features/bus-activation.md` (v0.1.0)
- Area: Buses
- Slice: `Features/Buses/DeactivateBus/`

## Contract
Implemented **verbatim** from the issued contract — if it looks wrong, report drift upstream
(`skills-spec-authoring`), never deviate locally.

- Operation: `deactivateBus` — `contracts/buses/openapi.yaml` → `contracts/buses/bus.yaml`
- Route / verb: `POST /buses/{busId}/deactivate`
- Request: path `busId` (uuid); no body
- Response: `200` with `Bus`
- Problem codes: `UNAUTHORIZED` (401), `FORBIDDEN` (403, caller not an operator),
  `OPERATOR_PROFILE_NOT_READY` (403), `NOT_FOUND` (404), `BUS_IN_USE` (409, with
  `futureTripCount` and `nextDepartureAt`)

## Acceptance criteria
Each criterion maps to tests in `DeactivateBus.Tests.cs`.

- [~] AC-1: Deactivate an unused bus → `DeactivateBus_ActiveBusWithoutTrips_BecomesInactive`,
  `DeactivateBus_OnlyDepartedAndCancelledTrips_BecomesInactive`. The handler touches `buses.buses`
  only, so no event is recorded. "Its public page still shows" is tested by GetPublicBus (CAP-BUS-011).
- [x] AC-2: A bus with future trips cannot be deactivated → `DeactivateBus_ThreeFutureScheduledTrips_IsRefusedBusInUseAndStaysActive`
- [x] AC-4: Repeating the same action changes nothing (deactivate) → `DeactivateBus_AlreadyInactive_ReturnsBusUnchanged`
- [x] AC-5: Unknown bus (deactivate) → `DeactivateBus_UnknownBus_IsRefusedNotFound`
- [~] AC-6: Another operator's bus (deactivate) → `DeactivateBus_AnotherOperatorsBus_IsReportedNotFoundAndLeftActive` —
  **deliberately 404, not the spec's FORBIDDEN** (see Known deviation)
- [x] AC-7: Profile not ready (deactivate) → `DeactivateBus_OperatorWithoutProfile_IsRefusedProfileNotReady`
- [x] AC-8: Only operators, signed in (deactivate) → `DeactivateBus_NotAnOperator_IsRefusedForbidden`, `DeactivateBus_WithoutAccessToken_IsRefusedUnauthorized`

## Invariants touched
- "Locked while in use" (buses shared rule 9): a future scheduled trip is a `Scheduled` trip whose
  departure is after the clock's now. The bus row is locked `FOR UPDATE` before the check, so a
  slice that gives the bus new trips must lock the same row (`FOR SHARE` or stronger).
- Data: this slice introduced `trips.schedules` (V0013) and `trips.trips` (V0014) so the check reads
  real trips; their class-fare and event tables arrive with CreateSchedule.

## Known deviation
- Another operator's bus answers `404 NOT_FOUND`, indistinguishable from a missing bus, per
  the RouteGo coding standard's rule that out-of-scope resources return 404, never 403. Decided by
  the developer on 2026-10-09; drift reported upstream against AC-6 and the contract's 403 description.
- A `busId` that is not a UUID answers `400 VALIDATION_FAILED` (the contract lists no 400 for this
  operation; model binding refuses the value before the slice runs).
