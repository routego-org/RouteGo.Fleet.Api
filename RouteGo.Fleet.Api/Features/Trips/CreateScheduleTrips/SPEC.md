# CAP-TRP-005 — Extend a schedule's trips

- Capability: CAP-TRP-005
- Canonical spec: `spec-RouteGo.Fleet.API/capabilities/trips/features/trip-extension.md` (v0.1.0)
- Area: Trips
- Slice: `Features/Trips/CreateScheduleTrips/`

## Contract
Implemented **verbatim** from the issued contract — if it looks wrong, report drift upstream
(`skills-spec-authoring`), never deviate locally.

- Operation: `createScheduleTrips` — `contracts/trips/openapi.yaml` → `contracts/trips/trip.yaml`
- Route / verb: `POST /schedules/{scheduleId}/trips`
- Request: path `scheduleId` (uuid); body `TripExtensionRequest` (`endDate`)
- Response: `201` with `TripExtension` (`scheduleId`, `tripsGeneratedThrough`, `items` of `Trip`); no Location
- Problem codes: `VALIDATION_FAILED` (400), `UNAUTHORIZED` (401), `FORBIDDEN` (403, caller not an
  operator), `OPERATOR_PROFILE_NOT_READY` (403), `NOT_FOUND` (404), `SCHEDULE_INACTIVE` (409),
  `BUS_INACTIVE` (422, `busId`), `TRIP_END_DATE_OUT_OF_RANGE` (422, `latestAllowedDate`),
  `BUS_TRIP_OVERLAP` (409, `conflicts`); plus `CLASS_FARES_MISMATCH` (422) — see Known deviation
- Events: one `TripScheduled` (full trip snapshot, `Domain/Trips/TripScheduledEvent`) per generated trip.

## Acceptance criteria
Each criterion maps to tests in `CreateScheduleTrips.Tests.cs`.

- [x] AC-1: Extend to an end date → `CreateScheduleTrips_ExtendToNovemberSixth_GeneratesTheSevenMissingTrips`,
  `CreateScheduleTrips_ExtendToNovemberSixth_ListsEachNewTripInFull`
- [x] AC-2: Repeat safety → `CreateScheduleTrips_AlreadyExtendedToNovemberSixth_GeneratesNothingMore`
- [x] AC-3: A trip the operator cancelled is not brought back → `CreateScheduleTrips_DateTheOperatorCancelled_IsNotBroughtBack`
- [x] AC-4: Dates cancelled by a deactivation are sold again → `CreateScheduleTrips_DatesCancelledByDeactivation_AreGeneratedAgain`
- [x] AC-5: End date limits → `CreateScheduleTrips_EndDateOutOfRange_IsRefusedWithTheLatestAllowedDate`
  (and its boundary, `CreateScheduleTrips_EndDateExactlySixtyDaysAhead_IsAccepted`)
- [x] AC-6: Inactive schedule or bus → `CreateScheduleTrips_InactiveSchedule_IsRefusedScheduleInactive`,
  `CreateScheduleTrips_InactiveBus_IsRefusedBusInactive`
- [x] AC-7: An inactive route does not stop an extension → `CreateScheduleTrips_InactiveRoute_StillGeneratesTheTrips`
- [x] AC-8: Overlap → `CreateScheduleTrips_BusOnAnotherTripAtTheTime_IsRefusedBusTripOverlapAndNothingChanges`
- [~] AC-9: Unknown or another operator's schedule → `CreateScheduleTrips_UnknownSchedule_IsRefusedNotFound`;
  `CreateScheduleTrips_AnotherOperatorsSchedule_IsReportedNotFoundAndLeftAlone` — **deliberately 404,
  not the spec's FORBIDDEN** (see Known deviation)
- [x] AC-10: Missing end date → `CreateScheduleTrips_MissingOrInvalidEndDate_IsRefusedNamingTheEndDate`
- [x] AC-11: Profile not ready, only operators, signed in → `CreateScheduleTrips_OperatorWithoutProfile_IsRefusedProfileNotReady`,
  `CreateScheduleTrips_NotAnOperator_IsRefusedForbidden`, `CreateScheduleTrips_WithoutAccessToken_IsRefusedUnauthorized`
- [x] Handed over by other slices: ReplaceBus AC-10 (trips generated afterwards carry the bus's new
  details) → `CreateScheduleTrips_BusRenamedBefore_NewTripsCarryTheNewName`; ActivateSchedule AC-3
  ("can be extended again") → `CreateScheduleTrips_ScheduleActivatedAgain_CanBeExtended`;
  DeactivateSchedule rule 2 (deactivation does not block dates) → AC-4's test.

## Invariants touched
- Check order (profile → not found → `SCHEDULE_INACTIVE` → `BUS_INACTIVE` → end date → fares → overlap)
  follows the capability's emits list.
- Locks: the schedule row, then the bus row, both `FOR UPDATE` and always in that order (as
  DeactivateSchedule locks the schedule and DeactivateBus/ReplaceBus/CreateSchedule lock the bus).
- A date is taken when the schedule has a `Scheduled` trip on it or an `Operator`-cancelled one;
  `ScheduleChanged` / `ScheduleDeactivated` cancellations leave it open.
- Generation and overlap are the shared `Domain/Trips/TripPlan`; the end date limit is
  `TripRules.MaxTripHorizonDays` (60). The generated-through date only ever moves forward (the UPDATE's
  `WHERE trips_generated_through < @EndDate`, so an unchanged date writes no audit entry).
- New trips take the schedule's stored fares (paise → rupees) and its current route, bus and departure.

## Known deviation
- Another operator's schedule answers `404 NOT_FOUND`, per the coding standard (out-of-scope → 404).
  Decided by the developer on 2026-10-09 for every slice; drift reported upstream against AC-9.
- **Stale fares:** if the bus's seat classes were replaced after the schedule's fares were set (possible
  once the bus has no future trips), the extension is refused `422 CLASS_FARES_MISMATCH` with
  `missingClasses` / `unknownClasses`, telling the operator to edit the schedule's fares first. The
  capability's emits list has no such code — spec gap; decided by the developer on 2026-10-10 and
  reported upstream as drift.
- `endDate` is bound as text and checked by the validator, so a malformed date is named `endDate` in
  `errors`. A `scheduleId` that is not a UUID answers `400 VALIDATION_FAILED` from model binding.
