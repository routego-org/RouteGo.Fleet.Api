# CAP-TRP-009 — Replace one trip's class fares

- Capability: CAP-TRP-009
- Canonical spec: `spec-RouteGo.Fleet.API/capabilities/trips/features/trip-fare-change.md` (v0.1.0)
- Area: Trips
- Slice: `Features/Trips/ReplaceTripFares/`

## Contract
Implemented **verbatim** from the issued contract — if it looks wrong, report drift upstream
(`skills-spec-authoring`), never deviate locally.

- Operation: `replaceTripFares` — `contracts/trips/openapi.yaml` → `contracts/trips/trip.yaml`
- Route / verb: `PUT /trips/{tripId}/fares`
- Request: path `tripId` (uuid); body `TripFaresRequest` (`classFares`)
- Response: `200` with `Trip`
- Problem codes: `VALIDATION_FAILED` (400), `UNAUTHORIZED` (401), `FORBIDDEN` (403, caller not an
  operator), `OPERATOR_PROFILE_NOT_READY` (403), `NOT_FOUND` (404), `TRIP_NOT_SCHEDULED` (409,
  `currentStatus`), `TRIP_DEPARTED` (409, `departedAt`), `FARE_CHANGE_CUTOFF_PASSED` (422, `cutoffAt`),
  `CLASS_FARES_MISMATCH` (422, `missingClasses`, `unknownClasses`)
- Events: one `FareChanged` (`tripId`, `classFares[{class, fare}]`, `seats[{seatNo, class, fare}]`,
  shared `Domain/Trips/FareChangedEvent`) when any class fare changed.

## Acceptance criteria
Each criterion maps to tests in `ReplaceTripFares.Tests.cs`.

- [x] AC-1: Change a trip's fares → `ReplaceTripFares_NewFares_ReturnsTheTripWithThem`,
  `ReplaceTripFares_NewFares_RecordsFareChangedWithBothClassesAndAllThirtySixSeats`,
  `ReplaceTripFares_NewFares_LeavesTheScheduleAndItsOtherTripsUnchanged` (and shared rule 4's matching
  ignoring case, `ReplaceTripFares_ClassNamesInAnotherCase_StoresTheBusSpelling`)
- [x] AC-2: Same fares — nothing announced → `ReplaceTripFares_SameFares_ReturnsTheTripUnchangedAndRecordsNoEvent`
- [x] AC-3: Inside the cutoff → `ReplaceTripFares_InsideTheCutoff_IsRefusedWithTheCutoffAndFaresUnchanged`
  (16:31 IST and exactly 16:30 IST); boundary `ReplaceTripFares_JustBeforeTheCutoff_IsAccepted` (16:29 IST)
- [x] AC-4: The trip has departed → `ReplaceTripFares_DepartedTrip_IsRefusedTripDeparted`
- [x] AC-5: The trip is cancelled → `ReplaceTripFares_CancelledTrip_IsRefusedTripNotScheduledWithItsStatus`
- [x] AC-6: Fares must cover exactly the bus's seat classes → `ReplaceTripFares_FaresNotMatchingTheBus_IsRefusedClassFaresMismatch`
- [x] AC-7: Fare limits → `ReplaceTripFares_FareBreakingARule_IsRefusedNamingTheFare`
- [~] AC-8: Unknown or another operator's trip → `ReplaceTripFares_UnknownTrip_IsRefusedNotFound`;
  `ReplaceTripFares_AnotherOperatorsTrip_IsReportedNotFoundAndLeftAlone` — **deliberately 404, not the
  spec's FORBIDDEN** (see Known deviation)
- [x] AC-9: Profile not ready, only operators, signed in → `ReplaceTripFares_OperatorWithoutProfile_IsRefusedProfileNotReady`,
  `ReplaceTripFares_NotAnOperator_IsRefusedForbidden`, `ReplaceTripFares_WithoutAccessToken_IsRefusedUnauthorized`

## Invariants touched
- Check order (business rule 2): profile → not found → cancelled (`TRIP_NOT_SCHEDULED`) → departed
  (`TRIP_DEPARTED`) → cutoff (`FARE_CHANGE_CUTOFF_PASSED`) → fares (`CLASS_FARES_MISMATCH`).
- Locks: the trip's schedule row `FOR UPDATE` first (the order every request that changes a schedule's
  trips takes: schedule, then bus), then the trip row `FOR UPDATE`. The bus is not locked: a fare change
  neither adds trips nor moves them, and the bus's seat classes cannot change while it has future trips.
- Cutoff: `TripRules.CanChangeFares` / `GetFareChangeCutoffAt` — fares change only while now is before
  departure minus 6 hours (`TripRules.FareChangeCutoff`); `cutoffAt` is that instant. Departed is
  derived (scheduled and departure at or before `IClock` now).
- Fares match the bus's classes ignoring case and are stored in the bus's spelling, in paise; only
  fares that differ are written (no audit entry for an unchanged fare); `FareChanged` is recorded only
  when at least one fare changed. The schedule and its other trips are never touched.
- The response reads the trip back: source and destination are the route's first and last stops,
  current bus and operator names, fares in the bus's seat-class order.

## Known deviation
- Another operator's trip answers `404 NOT_FOUND`, per the coding standard (out-of-scope → 404).
  Decided by the developer on 2026-10-09 for every slice; drift reported upstream against AC-8.
- A `tripId` that is not a UUID answers `400 VALIDATION_FAILED` from model binding.
