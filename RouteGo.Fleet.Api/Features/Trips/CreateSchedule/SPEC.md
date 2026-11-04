# CAP-TRP-001 — Create schedule

- Capability: CAP-TRP-001
- Canonical spec: `spec-RouteGo.Fleet.API/capabilities/trips/features/schedule-management.md` (v0.1.1)
- Area: Trips
- Slice: `Features/Trips/CreateSchedule/`

## Contract
Implemented **verbatim** from the issued contract — if it looks wrong, report drift upstream
(`skills-spec-authoring`), never deviate locally.

- Operation: `createSchedule` — `contracts/trips/openapi.yaml` → `contracts/trips/schedule.yaml`
- Route / verb: `POST /schedules`
- Request: body `ScheduleRequest`
- Response: `201` with `Schedule`, `Location: /schedules/{scheduleId}`
- Problem codes: `VALIDATION_FAILED` (400), `UNAUTHORIZED` (401), `FORBIDDEN` (403, caller not an
  operator), `OPERATOR_PROFILE_NOT_READY` (403), `UNKNOWN_ROUTE` (422, `routeId`), `ROUTE_INACTIVE`
  (422, `routeId`), `UNKNOWN_BUS` (422, `busId`), `BUS_INACTIVE` (422, `busId`),
  `CLASS_FARES_MISMATCH` (422, `missingClasses`, `unknownClasses`), `BUS_TRIP_OVERLAP` (409,
  `conflicts` of `serviceDate`, `tripId`, `departureAt`)
- Events: one `TripScheduled` per generated trip, recorded in `trips.fleet_events` in the same
  transaction (`architecture/fleet-events.md`); delivery is the publisher's (CAP-TRP-012, Features/Trips/PublishFleetEvents).

## Acceptance criteria
Each criterion maps to tests in `CreateSchedule.Tests.cs`. AC-8 to AC-17 belong to the other
schedule slices (list, read, replace).

- [x] AC-1: Create a schedule → `CreateSchedule_MondayWednesdayFriday_IsCreatedActiveThroughTwoWeeks`,
  `CreateSchedule_MondayWednesdayFriday_GeneratesSixTripsWithTheFares`,
  `CreateSchedule_MondayWednesdayFriday_RecordsTripScheduledForEveryTripWithOperatorAndBus`,
  `CreateSchedule_MondayWednesdayFriday_SnapshotCarriesTheRouteAndTimes`,
  `CreateSchedule_MondayWednesdayFriday_SnapshotPricesEverySeat`
- [x] AC-2: Today's departure has already passed → `CreateSchedule_TodaysDepartureHasPassed_GeneratesFromTheNextDayOfService`
- [x] AC-3: Fares must cover exactly the bus's seat classes (create) → `CreateSchedule_FaresNotNamingExactlyTheSeatClasses_IsRefusedClassFaresMismatch`
- [x] AC-4: Field rules (create) → `CreateSchedule_FieldBreaksARule_IsRefusedNamingTheField`
- [x] AC-5: Bus or route not usable (create) → `CreateSchedule_UnknownBus_IsRefusedUnknownBus`,
  `CreateSchedule_AnotherOperatorsBus_IsRefusedUnknownBus`, `CreateSchedule_UnknownRoute_IsRefusedUnknownRoute`,
  `CreateSchedule_AnotherOperatorsRoute_IsRefusedUnknownRoute`, `CreateSchedule_InactiveBus_IsRefusedBusInactive`,
  `CreateSchedule_InactiveRoute_IsRefusedRouteInactive`
- [x] AC-6: The bus would be on two trips at once (create) → `CreateSchedule_BusOnAnotherTripAtTheTime_IsRefusedBusTripOverlapAndNothingChanges`
- [x] AC-7: The turnaround buffer counts → `CreateSchedule_BusArrivesWithinTheTurnaroundBuffer_IsRefusedBusTripOverlap`
  (and its boundary, `CreateSchedule_BusFreeExactlyAtDeparture_IsCreated`)
- [x] AC-18: Profile not ready (create) → `CreateSchedule_OperatorWithoutProfile_IsRefusedProfileNotReady`
- [x] AC-19: Only operators, signed in (create) → `CreateSchedule_NotAnOperator_IsRefusedForbidden`,
  `CreateSchedule_WithoutAccessToken_IsRefusedUnauthorized`
- [x] Business rule 2 (check order: route before bus) → `CreateSchedule_UnknownRouteAndInactiveBus_IsRefusedForTheRouteFirst`
- [x] Shared rule 4 (classes matched ignoring case) → `CreateSchedule_FareClassInAnotherCase_IsKeptInTheBusSpelling`
- [x] Shared rule 6 (cancelled trips excepted; the same schedule included) →
  `CreateSchedule_CancelledTripAtTheTime_DoesNotBlockTheBus`, `CreateSchedule_OwnTripsOverlapEachOther_IsRefusedNamingTheEarlierTrip`
- [x] Handed over by other slices: DeactivateRoute AC-7 (new schedule on an inactive route →
  ROUTE_INACTIVE) → `CreateSchedule_InactiveRoute_IsRefusedRouteInactive`; ActivateBus AC-3 (an
  activated bus can be scheduled) → `CreateSchedule_BusActivatedAgain_CanBeScheduled`; ReplaceBus AC-10
  (trips generated afterwards carry the bus's new name, AC and amenities) →
  `CreateSchedule_BusDetailsChangedBefore_TripsCarryTheNewDetails`

## Invariants touched
- Field and fare rules, the 14 trip generation days, the 60-minute turnaround buffer, IST service
  dates and the overlap test: `Domain/Trips/TripRules` (the validator delegates to it). The overlap
  conflict entry is `Domain/Trips/TripConflict`, shared with ProblemResult.
- Trip generation and overlap detection are pure and shared with CreateScheduleTrips
  (`Domain/Trips/TripPlan`); the handler supplies the clock's now and the bus's other trips. A date is
  skipped when its departure is not after now. The TripScheduled snapshot is `Domain/Trips/TripScheduledEvent`
  (moved out of this slice on 2026-10-10 when CreateScheduleTrips needed it — decided by the developer).
- Concurrency: the bus row is locked `FOR UPDATE` (as DeactivateBus and ReplaceBus do) before the
  bus's trips are read, so two schedules for one bus cannot both pass the overlap check; the route
  row is locked `FOR SHARE`.
- Trip and schedule ids are assigned in C# before the overlap check, so a conflict between two of the
  new schedule's own trips names the earlier one (decided by the developer on 2026-10-10).
- Class fares keep the bus's spelling of each class, whatever case the request used (decided by the
  developer on 2026-10-10). Fares are stored in paise (`bigint`, ×100) and shown in whole rupees.
- Data: this slice introduced `trips.schedule_class_fares` (V0015), `trips.trip_class_fares` (V0016)
  and `trips.fleet_events` (V0017, excluded from audit, `data` as `jsonb`, `transaction_id xid8` for
  commit-order publishing).

## Known deviation
- None from the spec. A `fare` with a fraction (₹999.50) is refused by the validator naming the field,
  so the request accepts any number for `fare` and the contract's integer rule is enforced there. An
  unknown day name or a malformed `routeId`/`busId` is refused `400 VALIDATION_FAILED` by model binding.
