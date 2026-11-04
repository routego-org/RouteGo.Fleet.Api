# CAP-TRP-003 — Get schedule

- Capability: CAP-TRP-003
- Canonical spec: `spec-RouteGo.Fleet.API/capabilities/trips/features/schedule-management.md` (v0.1.1)
- Area: Trips
- Slice: `Features/Trips/GetSchedule/`

## Contract
Implemented **verbatim** from the issued contract — if it looks wrong, report drift upstream
(`skills-spec-authoring`), never deviate locally.

- Operation: `getSchedule` — `contracts/trips/openapi.yaml` → `contracts/trips/schedule.yaml`
- Route / verb: `GET /schedules/{scheduleId}`
- Request: path `scheduleId` (uuid)
- Response: `200` with `Schedule`
- Problem codes: `UNAUTHORIZED` (401), `FORBIDDEN` (403, caller not an operator),
  `OPERATOR_PROFILE_NOT_READY` (403), `NOT_FOUND` (404)

## Acceptance criteria
Each criterion maps to tests in `GetSchedule.Tests.cs`.

- [x] AC-10: Read one schedule → `GetSchedule_OwnSchedule_ReturnsEveryDetail`,
  `GetSchedule_OwnSchedule_ReturnsFaresInRupeesInTheBusSeatClassOrder`
- [~] AC-17: Unknown or another operator's schedule (read) → `GetSchedule_UnknownSchedule_IsRefusedNotFound`;
  `GetSchedule_AnotherOperatorsSchedule_IsReportedNotFound` — **deliberately 404, not the spec's
  FORBIDDEN** (see Known deviation). The replace half of AC-17 is tested by ReplaceSchedule
  (`ReplaceSchedule_UnknownSchedule_IsRefusedNotFound`, `ReplaceSchedule_AnotherOperatorsSchedule_IsReportedNotFoundAndLeftAlone`).
- [x] AC-18: Profile not ready (read) → `GetSchedule_OperatorWithoutProfile_IsRefusedProfileNotReady`
- [x] AC-19: Only operators, signed in (read) → `GetSchedule_NotAnOperator_IsRefusedForbidden`,
  `GetSchedule_WithoutAccessToken_IsRefusedUnauthorized`

## Invariants touched
- None in `Domain/` beyond `TripRules.PaisePerRupee`; ownership is in the lookup's `WHERE` clause.
- Class fares are stored in paise and shown in whole rupees, ordered by the bus's seat-class display
  order; a fare whose class the bus no longer has comes last, by name (decided by the developer on
  2026-10-10).

## Known deviation
- Another operator's schedule answers `404 NOT_FOUND`, indistinguishable from a missing schedule, per
  the RouteGo coding standard's rule that out-of-scope resources return 404, never 403. Decided by the
  developer on 2026-10-09 for every slice; drift reported upstream against AC-17 and the contract's
  403 description.
- A `scheduleId` that is not a UUID answers `400 VALIDATION_FAILED` (the contract lists no 400 for this
  operation; model binding refuses the value before the slice runs).
