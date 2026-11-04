# CAP-TRP-007 — Activate schedule

- Capability: CAP-TRP-007
- Canonical spec: `spec-RouteGo.Fleet.API/capabilities/trips/features/schedule-activation.md` (v0.1.1)
- Area: Trips
- Slice: `Features/Trips/ActivateSchedule/`

## Contract
Implemented **verbatim** from the issued contract — if it looks wrong, report drift upstream
(`skills-spec-authoring`), never deviate locally.

- Operation: `activateSchedule` — `contracts/trips/openapi.yaml` → `contracts/trips/schedule.yaml`
- Route / verb: `POST /schedules/{scheduleId}/activate`
- Request: path `scheduleId` (uuid); no body
- Response: `200` with `Schedule`
- Problem codes: `UNAUTHORIZED` (401), `FORBIDDEN` (403, caller not an operator),
  `OPERATOR_PROFILE_NOT_READY` (403), `NOT_FOUND` (404)

## Acceptance criteria
Each criterion maps to tests in `ActivateSchedule.Tests.cs`.

- [x] AC-3: Activate a schedule → `ActivateSchedule_InactiveSchedule_BecomesActiveAndGeneratesNoTrips`.
  "Can be extended again" is tested by CreateScheduleTrips (`CreateScheduleTrips_ScheduleActivatedAgain_CanBeExtended`);
  "can be edited again" by ReplaceSchedule (`ReplaceSchedule_ScheduleActivatedAgain_CanBeEdited`).
- [x] AC-4: Activating an active schedule changes nothing → `ActivateSchedule_AlreadyActive_ReturnsScheduleUnchanged`
- [~] AC-5: Unknown or another operator's schedule (activate) → `ActivateSchedule_UnknownSchedule_IsRefusedNotFound`;
  `ActivateSchedule_AnotherOperatorsSchedule_IsReportedNotFoundAndLeftInactive` — **deliberately 404,
  not the spec's FORBIDDEN** (see Known deviation). The deactivate half is tested by DeactivateSchedule.
- [x] AC-6: Profile not ready, only operators, signed in (activate) → `ActivateSchedule_OperatorWithoutProfile_IsRefusedProfileNotReady`,
  `ActivateSchedule_NotAnOperator_IsRefusedForbidden`, `ActivateSchedule_WithoutAccessToken_IsRefusedUnauthorized`
- [x] Business rule 3 (no preconditions: bus or route may be inactive) → `ActivateSchedule_InactiveBusAndRoute_StillBecomesActive`

## Invariants touched
- None in `Domain/`. Ownership and "only an inactive schedule changes" are in the UPDATE's `WHERE`
  clause, so a repeat activation keeps `updatedAt` and writes no audit entry. No trip is generated and
  no event recorded.
- Class fares are shown as GetSchedule shows them: whole rupees, in the bus's seat-class order.

## Known deviation
- Another operator's schedule answers `404 NOT_FOUND`, indistinguishable from a missing schedule, per
  the RouteGo coding standard's rule that out-of-scope resources return 404, never 403. Decided by the
  developer on 2026-10-09 for every slice; drift reported upstream against AC-5 and the contract's
  403 description.
- A `scheduleId` that is not a UUID answers `400 VALIDATION_FAILED` (the contract lists no 400 for this
  operation; model binding refuses the value before the slice runs).
