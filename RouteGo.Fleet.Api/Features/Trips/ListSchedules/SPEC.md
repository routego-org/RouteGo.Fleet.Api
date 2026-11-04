# CAP-TRP-002 — List schedules

- Capability: CAP-TRP-002
- Canonical spec: `spec-RouteGo.Fleet.API/capabilities/trips/features/schedule-management.md` (v0.1.1)
- Area: Trips
- Slice: `Features/Trips/ListSchedules/`

## Contract
Implemented **verbatim** from the issued contract — if it looks wrong, report drift upstream
(`skills-spec-authoring`), never deviate locally.

- Operation: `listSchedules` — `contracts/trips/openapi.yaml` → `contracts/trips/schedule.yaml`
- Route / verb: `GET /schedules`
- Request: query `status` (`ScheduleStatus`, optional), `routeId` (uuid, optional), `busId` (uuid,
  optional), `page` (≥ 1, default 1), `pageSize` (1–200, default 20)
- Response: `200` with `ScheduleListResponse` (`items` of `Schedule`, `totalCount`, `page`, `pageSize`)
- Problem codes: `VALIDATION_FAILED` (400), `UNAUTHORIZED` (401), `FORBIDDEN` (403, caller not an
  operator), `OPERATOR_PROFILE_NOT_READY` (403)

## Acceptance criteria
Each criterion maps to tests in `ListSchedules.Tests.cs`.

- [x] AC-8: List own schedules → `ListSchedules_ActiveAndInactiveOwnSchedules_ReturnsBothButNotAnotherOperatorsSchedule`,
  `ListSchedules_StatusInactive_ReturnsOnlyTheInactiveSchedule`, `ListSchedules_ByRoute_ReturnsOnlyThatRoutesSchedules`,
  `ListSchedules_ByBus_ReturnsOnlyThatBussSchedules`
- [x] AC-9: No schedules yet → `ListSchedules_NoSchedulesYet_ReturnsAnEmptyPageWithTotalCountZero`
- [x] AC-18: Profile not ready (list) → `ListSchedules_OperatorWithoutProfile_IsRefusedProfileNotReady`
- [x] AC-19: Only operators, signed in (list) → `ListSchedules_NotAnOperator_IsRefusedForbidden`,
  `ListSchedules_WithoutAccessToken_IsRefusedUnauthorized`
- [x] Business rule 6 (sorted by departure time then creation time, paged) → `ListSchedules_PagesOfTwo_AreSortedByDepartureTime`
- [x] Query validation (contract's 400) → `ListSchedules_InvalidQuery_IsRefusedValidationFailedNamingTheField`

## Invariants touched
- Paging limits and defaults: `Common/Paging.cs` (out-of-range → `VALIDATION_FAILED`).
- Ownership is the query's `WHERE operator_id = @OperatorId`; a route or bus filter naming another
  operator's record simply matches nothing.
- Sort: `departure_time` (HH:mm, so text order is time order), then `created_at`, then
  `schedule_id` as a stable tie-break.
- Class fares: stored in paise, shown in whole rupees, in the bus's seat-class display order (as
  GetSchedule); read for the whole page in one query.

## Notes
- `status` must be spelled exactly as the contract's enum (`active`, `inactive`).
- `routeId` and `busId` are bound as text and checked by the validator, so a malformed one is named
  `routeId` / `busId` in `errors` rather than by the C# property name model binding would use.
