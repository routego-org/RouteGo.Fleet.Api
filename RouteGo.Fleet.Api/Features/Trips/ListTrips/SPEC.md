# CAP-TRP-008 — List the operator's own trips

- Capability: CAP-TRP-008
- Canonical spec: `spec-RouteGo.Fleet.API/capabilities/trips/features/operator-trips.md` (v0.1.0)
- Area: Trips
- Slice: `Features/Trips/ListTrips/`

## Contract
Implemented **verbatim** from the issued contract — if it looks wrong, report drift upstream
(`skills-spec-authoring`), never deviate locally.

- Operation: `listTrips` — `contracts/trips/openapi.yaml` → `contracts/trips/trip.yaml`
- Route / verb: `GET /trips`
- Request: query `from`, `to` (date), `scheduleId`, `busId` (uuid), `status` (`TripStatus`), `page`, `pageSize`
- Response: `200` with `TripListResponse` (`items` of `Trip`, `totalCount`, `page`, `pageSize`)
- Problem codes: `VALIDATION_FAILED` (400), `UNAUTHORIZED` (401), `FORBIDDEN` (403, caller not an
  operator), `OPERATOR_PROFILE_NOT_READY` (403)

## Acceptance criteria
Each criterion maps to tests in `ListTrips.Tests.cs`.

- [x] AC-1: Default range → `ListTrips_WithoutDates_ReturnsTodayThroughThirteenDaysAheadByDeparture`,
  `ListTrips_ScheduledTrip_ShowsEveryDetail`, `ListTrips_CancelledTrip_ShowsItsReasonAndCancellationTime`
- [x] AC-2: Status is reported, departed included → `ListTrips_DepartedScheduledAndCancelledTrips_ReportsAndFiltersEachStatus`
  (also covers the cancelled filter)
- [x] AC-3: Filter by schedule or bus → `ListTrips_FilteredByScheduleOrBus_ReturnsOnlyThatSchedulesOrBussTrips`
- [x] AC-4: Only own trips → `ListTrips_AnotherOperatorHasTripsInTheRange_ReturnsOnlyOwnTrips`
- [x] AC-5: Nothing in range → `ListTrips_RangeWithNoTrips_ReturnsAnEmptyPage`
- [x] AC-6: Date range rules → `ListTrips_InvalidRangeOrFilter_IsRefusedNamingTheField` (and the boundary,
  `ListTrips_RangeOfExactlyThirtyOneDays_IsAccepted`)
- [x] AC-7: Profile not ready, only operators, signed in → `ListTrips_OperatorWithoutProfile_IsRefusedProfileNotReady`,
  `ListTrips_NotAnOperator_IsRefusedForbidden`, `ListTrips_WithoutAccessToken_IsRefusedUnauthorized`
- [x] Business rule 4 (sorted, paged) → `ListTrips_SecondPageOfOne_ReturnsTheSecondTripByDeparture`
- [x] Developer decision (only one end given) → `ListTrips_OnlyOneEndGiven_CoversFourteenDaysFromThatEnd`

## Invariants touched
- Ownership is the SQL `WHERE t.operator_id = @OperatorId`; the profile gate runs first.
- The range rules are shared with SearchTrips (CAP-TRP-011): `TripRules.GetListRange` (default today
  through today plus 13 days, `DefaultListDays` = 14) and `TripRules.IsAcceptableListRange` (forwards,
  at most `MaxListDays` = 31 counting both ends), with `Domain/Trips/ServiceDateRange`.
- Departed is derived, never stored: a `Scheduled` trip departing at or before `IClock` now is
  reported `departed`; the status filter becomes a stored status plus a departure bound in SQL.
- Sorted by departure, then trip id, so paging never repeats or skips a trip.
- Source and destination are the route's first and last stops; the bus name and operator name are
  current values; class fares are shown in whole rupees in the bus's seat-class order (a class the bus
  has since dropped goes last, by name).
- Dates, ids and the status arrive as text and are checked by the validator, so a malformed one is
  named by its contract field; status spellings are the contract's camelCase only (`WireJson.TryReadEnum`).

## Known deviation
- **Only one end of the range given** (the spec defines only the no-dates default): the 14-day default
  window runs from the given end — only `from`: `from` through `from` + 13 days; only `to`: `to` − 13 days
  through `to`. Decided by the developer on 2026-10-10; to be reported upstream as a spec gap.
- Both range refusals (reversed, longer than 31 days) are named under `to`, the field whose contract
  description carries the rule; the spec says only "naming the date range".
