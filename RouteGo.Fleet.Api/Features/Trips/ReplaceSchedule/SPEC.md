# CAP-TRP-004 — Replace a schedule and rewrite its future trips

- Capability: CAP-TRP-004
- Canonical spec: `spec-RouteGo.Fleet.API/capabilities/trips/features/schedule-management.md` (v0.1.1)
- Area: Trips
- Slice: `Features/Trips/ReplaceSchedule/`

## Contract
Implemented **verbatim** from the issued contract — if it looks wrong, report drift upstream
(`skills-spec-authoring`), never deviate locally.

- Operation: `replaceSchedule` — `contracts/trips/openapi.yaml` → `contracts/trips/schedule.yaml`
- Route / verb: `PUT /schedules/{scheduleId}`
- Request: path `scheduleId` (uuid); body `ScheduleRequest` (`routeId`, `busId`, `departureTime`, `daysOfWeek`, `classFares`)
- Response: `200` with `ScheduleChange` (`schedule`, `createdTripCount`, `cancelledTripCount`, `repricedTripCount`)
- Problem codes: `VALIDATION_FAILED` (400), `UNAUTHORIZED` (401), `FORBIDDEN` (403, caller not an
  operator), `OPERATOR_PROFILE_NOT_READY` (403), `NOT_FOUND` (404), `SCHEDULE_INACTIVE` (409),
  `UNKNOWN_BUS` (422, `busId`), `UNKNOWN_ROUTE` (422, `routeId`), `BUS_INACTIVE` (422, `busId`),
  `ROUTE_INACTIVE` (422, `routeId`), `CLASS_FARES_MISMATCH` (422, `missingClasses`, `unknownClasses`),
  `BUS_TRIP_OVERLAP` (409, `conflicts`)
- Events: `TripCancelled` (`tripId`, `reason` "Schedule changed") per cancelled trip; `TripScheduled`
  (full trip snapshot, `Domain/Trips/TripScheduledEvent`) per generated trip; `FareChanged` (`tripId`,
  `classFares[{class, fare}]`, `seats[{seatNo, class, fare}]`) per re-priced trip.

## Acceptance criteria
Each criterion maps to tests in `ReplaceSchedule.Tests.cs`.

- [x] AC-3: Fares must cover exactly the bus's seat classes (replace) →
  `ReplaceSchedule_FaresNotMatchingTheBus_IsRefusedClassFaresMismatchAndNothingChanges`
- [x] AC-4: Field rules (replace) → `ReplaceSchedule_FieldBreakingARule_IsRefusedNamingTheField`
- [x] AC-5: Bus or route not usable (replace) → `ReplaceSchedule_UnknownOrAnotherOperatorsBus_IsRefusedUnknownBus`,
  `ReplaceSchedule_UnknownOrAnotherOperatorsRoute_IsRefusedUnknownRoute`, `ReplaceSchedule_InactiveBus_IsRefusedBusInactive`,
  `ReplaceSchedule_MovedToAnInactiveRoute_IsRefusedRouteInactive` (and business rule 3's other half,
  `ReplaceSchedule_ItsOwnRouteDeactivatedSince_IsStillAccepted`)
- [x] AC-6: The bus would be on two trips at once (replace) →
  `ReplaceSchedule_BusOnAnotherTripAtTheTime_IsRefusedBusTripOverlapAndNothingChanges` (the schedule moves
  from bus "Beta" onto "Alpha", which has another schedule's trip on 2026-10-12 21:00–00:30 IST)
- [x] AC-11: Change the departure time → `ReplaceSchedule_DepartureTimeChanged_ReportsSixCreatedAndSixCancelled`,
  `ReplaceSchedule_DepartureTimeChanged_CancelsTheOldTripsAndGeneratesNewOnesWithTheirEvents`,
  `ReplaceSchedule_DepartureTimeChanged_AnnouncesEachNewTripWithItsSnapshot`
- [x] AC-12: Change the days → `ReplaceSchedule_DaysChangedToMondayAndSaturday_ChangesOnlyTheAffectedDates`
- [x] AC-13: A fare-only change re-prices in place → `ReplaceSchedule_FareOnlyChangeAtSixPm_RepricesAllButTheTripInsideTheCutoff`,
  `ReplaceSchedule_FareOnlyChange_RecordsTheNewFaresInFareChanged`
- [x] AC-14: Nothing changed → `ReplaceSchedule_SameDetails_ChangesNoTripAndRecordsNoEvent`
- [x] AC-15: Past and cancelled trips are never touched → `ReplaceSchedule_DepartedAndOperatorCancelledTrips_AreNeverTouchedNorRegenerated`
- [x] AC-16: An inactive schedule cannot be edited → `ReplaceSchedule_InactiveSchedule_IsRefusedScheduleInactive`
- [~] AC-17: Unknown or another operator's schedule (replace) → `ReplaceSchedule_UnknownSchedule_IsRefusedNotFound`;
  `ReplaceSchedule_AnotherOperatorsSchedule_IsReportedNotFoundAndLeftAlone` — **deliberately 404, not the
  spec's FORBIDDEN** (see Known deviation)
- [x] AC-18: Profile not ready (replace) → `ReplaceSchedule_OperatorWithoutProfile_IsRefusedProfileNotReady`
- [x] AC-19: Only operators, signed in (replace) → `ReplaceSchedule_NotAnOperator_IsRefusedForbidden`,
  `ReplaceSchedule_WithoutAccessToken_IsRefusedUnauthorized`
- [x] Handed over by other slices: ActivateSchedule AC-3 ("can be edited again") →
  `ReplaceSchedule_ScheduleActivatedAgain_CanBeEdited`; GetSchedule AC-17's replace half → AC-17's tests above.

## Invariants touched
- Check order: profile → not found → `SCHEDULE_INACTIVE` → bus (`UNKNOWN_BUS`, `BUS_INACTIVE`) → route
  (`UNKNOWN_ROUTE`, `ROUTE_INACTIVE`) → fares → overlap, following the capability's emits list.
- Locks: the schedule row, then the bus row, both `FOR UPDATE` and always in that order (as
  CreateScheduleTrips and DeactivateSchedule); the route `FOR SHARE` afterwards (as CreateSchedule).
- The bus must be the operator's and active even when unchanged; the route must be active only when
  it changes (an unchanged route may have been deactivated since).
- A future trip (`Scheduled`, departure after `IClock` now) is cancelled (cause `ScheduleChanged`,
  reason "Schedule changed") when its route, bus or departure differs from the new details, or its
  date is no longer a day of service. A kept future trip whose fares differ takes the new fares
  unless `TripRules.CanChangeFares` says the 6-hour cutoff (`TripRules.FareChangeCutoff`) has passed.
  Departed and cancelled trips are never changed.
- New trips: every day of service from today through the unchanged generated-through date without a
  remaining `Scheduled` trip (departed ones count) or an `Operator`-cancelled one, departing in the
  future — the shared `TripPlan.GenerateTrips`.
- Overlap counts the bus's `Scheduled` trips minus the ones this edit cancels, plus the new trips
  among themselves (`TripPlan.FindConflicts`); refused whole, nothing written.
- Writes are set-based and idempotent: the schedule row is updated only when route, bus, departure or
  days differ; schedule fares are upserted only where they differ (classes the new bus lacks are
  deleted first); trip fares change only where they differ. So an unchanged replacement writes no
  audit entry and records no event. Cancellations run before the inserts (one `Scheduled` trip per
  schedule and date).
- Fares stored in paise, shown and announced in whole rupees; class names in the bus's spelling.
- `FareChanged` data comes from the shared `Domain/Trips/FareChangedEvent` (also used by ReplaceTripFares).

## Known deviation
- Another operator's schedule answers `404 NOT_FOUND`, per the coding standard (out-of-scope → 404).
  Decided by the developer on 2026-10-09 for every slice; drift reported upstream against AC-17.
- A `scheduleId` that is not a UUID answers `400 VALIDATION_FAILED` from model binding.
