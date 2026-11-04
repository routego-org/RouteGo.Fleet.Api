# CAP-TRP-006 — Deactivate schedule

- Capability: CAP-TRP-006
- Canonical spec: `spec-RouteGo.Fleet.API/capabilities/trips/features/schedule-activation.md` (v0.1.1)
- Area: Trips
- Slice: `Features/Trips/DeactivateSchedule/`

## Contract
Implemented **verbatim** from the issued contract — if it looks wrong, report drift upstream
(`skills-spec-authoring`), never deviate locally.

- Operation: `deactivateSchedule` — `contracts/trips/openapi.yaml` → `contracts/trips/schedule.yaml`
- Route / verb: `POST /schedules/{scheduleId}/deactivate`
- Request: path `scheduleId` (uuid); no body
- Response: `200` with `ScheduleChange` (`schedule`, `createdTripCount`, `cancelledTripCount`, `repricedTripCount`)
- Problem codes: `UNAUTHORIZED` (401), `FORBIDDEN` (403, caller not an operator),
  `OPERATOR_PROFILE_NOT_READY` (403), `NOT_FOUND` (404)
- Events: one `TripCancelled` (`tripId`, `reason` "Schedule deactivated") per cancelled trip, recorded
  in `trips.fleet_events` in the same transaction; delivery is the publisher's (CAP-TRP-012, Features/Trips/PublishFleetEvents).

## Acceptance criteria
Each criterion maps to tests in `DeactivateSchedule.Tests.cs`.

- [x] AC-1: Deactivate a schedule → `DeactivateSchedule_ActiveScheduleWithFutureTrips_BecomesInactiveAndReportsFiveCancelled`,
  `DeactivateSchedule_ActiveScheduleWithFutureTrips_CancelsOnlyTheFutureTripsAndRecordsTheirEvents`
- [x] AC-2: Deactivating twice changes nothing → `DeactivateSchedule_AlreadyInactive_ChangesNothingAndReportsNoneCancelled`
- [~] AC-5: Unknown or another operator's schedule (deactivate) → `DeactivateSchedule_UnknownSchedule_IsRefusedNotFound`;
  `DeactivateSchedule_AnotherOperatorsSchedule_IsReportedNotFoundAndLeftActive` — **deliberately 404,
  not the spec's FORBIDDEN** (see Known deviation)
- [x] AC-6: Profile not ready, only operators, signed in (deactivate) → `DeactivateSchedule_OperatorWithoutProfile_IsRefusedProfileNotReady`,
  `DeactivateSchedule_NotAnOperator_IsRefusedForbidden`, `DeactivateSchedule_WithoutAccessToken_IsRefusedUnauthorized`
- Business rule 2's "trips cancelled this way do not block their dates from a later extension" is
  tested by CreateScheduleTrips (`CreateScheduleTrips_DatesCancelledByDeactivation_AreGeneratedAgain`);
  this slice records the cause `ScheduleDeactivated` that rule reads.

## Invariants touched
- "Future trip" = a `Scheduled` trip whose departure is after the clock's now; departed and cancelled
  trips are never touched. Cancellation time comes from `IClock`.
- The schedule row is locked `FOR UPDATE` before anything changes, so an edit or extension of the same
  schedule cannot add trips mid-deactivation (ReplaceSchedule and CreateScheduleTrips take the same
  lock).
- Cancellation and its events are one set-based statement (`UPDATE ... RETURNING` feeding the event
  `INSERT`); event data is built with `jsonb_build_object`.
- Only an active schedule is updated, so a repeat deactivation writes no audit entry and records no event.

## Known deviation
- Another operator's schedule answers `404 NOT_FOUND`, indistinguishable from a missing schedule, per
  the RouteGo coding standard's rule that out-of-scope resources return 404, never 403. Decided by the
  developer on 2026-10-09 for every slice; drift reported upstream against AC-5 and the contract's
  403 description.
- A `scheduleId` that is not a UUID answers `400 VALIDATION_FAILED` (the contract lists no 400 for this
  operation; model binding refuses the value before the slice runs).
