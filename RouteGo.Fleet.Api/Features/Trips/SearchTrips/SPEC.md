# CAP-TRP-011 — Search every operator's trips (Admin)

- Capability: CAP-TRP-011
- Canonical spec: `spec-RouteGo.Fleet.API/capabilities/trips/features/admin-trip-search.md` (v0.1.0)
- Area: Trips
- Slice: `Features/Trips/SearchTrips/`

## Contract
Implemented **verbatim** from the issued contract — if it looks wrong, report drift upstream
(`skills-spec-authoring`), never deviate locally.

- Operation: `searchTrips` — `contracts/trips/openapi.yaml` → `contracts/trips/trip.yaml`
- Route / verb: `GET /admin/trips`
- Request: query `operatorId`, `sourceCityId`, `destinationCityId` (uuid), `from`, `to` (date), `status`
  (`TripStatus`), `page`, `pageSize`
- Response: `200` with `TripListResponse` (`items` of `Trip`, `totalCount`, `page`, `pageSize`)
- Problem codes: `VALIDATION_FAILED` (400), `UNAUTHORIZED` (401), `FORBIDDEN` (403, caller not an admin)

## Acceptance criteria
Each criterion maps to tests in `SearchTrips.Tests.cs`.

- [x] AC-1: Search by route and date → `SearchTrips_BySourceDestinationAndDate_ReturnsEveryOperatorsTripsThatDayByDeparture`,
  `SearchTrips_CancelledTrip_ShowsEveryDetailWithReasonAndTime` (and the out-of-scope note on intermediate
  stops, `SearchTrips_DestinationIsAnIntermediateStop_FindsNothing`)
- [x] AC-2: One operator's cancelled trips → `SearchTrips_OneOperatorsCancelledTripsInARange_ReturnsOnlyThose`
  (and every status, `SearchTrips_DepartedScheduledAndCancelledTrips_ReportsAndFiltersEachStatus`)
- [x] AC-3: Default range → `SearchTrips_WithoutDates_ReturnsTodayThroughThirteenDaysAhead`
- [x] AC-4: No match is an empty page → `SearchTrips_RouteAndDateWithNoTrips_ReturnsAnEmptyPage`
- [x] AC-5: Date range rules → `SearchTrips_InvalidRangeOrFilter_IsRefusedNamingTheField`
- [x] AC-6: Only admins → `SearchTrips_NotAnAdmin_IsRefusedForbidden`
- [x] AC-7: Must be signed in → `SearchTrips_WithoutAccessToken_IsRefusedUnauthorized`
- [x] Developer decision (only one end given, as ListTrips) → `SearchTrips_OnlyOneEndGiven_CoversFourteenDaysFromThatEnd`

## Invariants touched
- Admin only: the new `Policies.Admin` (role Admin), registered in `Program.cs` with this first admin slice.
  No operator scope and no profile gate — the capability emits neither.
- The range rules are the shared `TripRules.GetListRange` / `IsAcceptableListRange` (as ListTrips).
- Source and destination filters match the route's first and last stops only; the operator filter
  compares the stored text id with the UUID's canonical lower-case form.
- Departed is derived from `IClock` now; the status filter becomes a stored status plus a departure bound.
- Sorted by departure, then trip id; paged with the shared paging parameters. Read only (reader connection).
- Tests use 2031 service dates, which no other test seeds, because the search sees every operator's trips
  in the shared test database.

## Known deviation
- **Only one end of the range given**: the 14-day default window runs from the given end, exactly as
  ListTrips (the spec defines only the no-dates default; rule 3 says the range follows ListTrips' rule).
  Decided by the developer on 2026-10-10; reported upstream as a spec gap.
- Both range refusals are named under `to`, the field whose contract description carries the rule.
