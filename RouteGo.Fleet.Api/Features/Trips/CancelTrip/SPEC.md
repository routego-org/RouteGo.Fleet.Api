# CAP-TRP-010 — Cancel one trip with a reason

- Capability: CAP-TRP-010
- Canonical spec: `spec-RouteGo.Fleet.API/capabilities/trips/features/trip-cancellation.md` (v0.1.0)
- Area: Trips
- Slice: `Features/Trips/CancelTrip/`

## Contract
Implemented **verbatim** from the issued contract — if it looks wrong, report drift upstream
(`skills-spec-authoring`), never deviate locally.

- Operation: `cancelTrip` — `contracts/trips/openapi.yaml` → `contracts/trips/trip.yaml`
- Route / verb: `POST /trips/{tripId}/cancel`
- Request: path `tripId` (uuid); body `TripCancellation` (`reason`)
- Response: `200` with `Trip` (status `cancelled`, `cancelReason`, `cancelledAt`)
- Problem codes: `VALIDATION_FAILED` (400), `UNAUTHORIZED` (401), `FORBIDDEN` (403, caller not an
  operator), `OPERATOR_PROFILE_NOT_READY` (403), `NOT_FOUND` (404), `TRIP_NOT_SCHEDULED` (409,
  `currentStatus`), `TRIP_DEPARTED` (409, `departedAt`)
- Events: one `TripCancelled` (`tripId`, `reason`).

## Acceptance criteria
Each criterion maps to tests in `CancelTrip.Tests.cs`.

- [x] AC-1: Cancel a trip → `CancelTrip_ScheduledTripDepartingInTwoHours_ReturnsItCancelledWithReasonAndTime`,
  `CancelTrip_ScheduledTrip_RecordsTripCancelledWithTheTripAndReason`
- [x] AC-2: The date is not sold again → `CancelTrip_ScheduleExtendedAfterwards_GeneratesNoTripForTheCancelledDate`,
  `CancelTrip_ScheduleEditedAfterwards_GeneratesNoTripForTheCancelledDate`
- [x] AC-3: Already cancelled → `CancelTrip_AlreadyCancelled_IsRefusedTripNotScheduledAndRecordsNoEvent`
- [x] AC-4: Already departed → `CancelTrip_DepartureTimeReachedOrPassed_IsRefusedTripDeparted` (and business
  rule 2's "right up to its departure time", `CancelTrip_OneMinuteBeforeDeparture_IsAccepted`)
- [x] AC-5: Reason rules → `CancelTrip_MissingOrBadLengthReason_IsRefusedNamingTheReason` (and the limits,
  `CancelTrip_ReasonAtTheLengthLimits_IsAccepted`)
- [~] AC-6: Unknown or another operator's trip → `CancelTrip_UnknownTrip_IsRefusedNotFound`;
  `CancelTrip_AnotherOperatorsTrip_IsReportedNotFoundAndLeftScheduled` — **deliberately 404, not the
  spec's FORBIDDEN** (see Known deviation)
- [x] AC-7: Profile not ready, only operators, signed in → `CancelTrip_OperatorWithoutProfile_IsRefusedProfileNotReady`,
  `CancelTrip_NotAnOperator_IsRefusedForbidden`, `CancelTrip_WithoutAccessToken_IsRefusedUnauthorized`

## Invariants touched
- Check order: profile → not found → already cancelled (`TRIP_NOT_SCHEDULED`) → departed (`TRIP_DEPARTED`,
  departure at or before `IClock` now). No cutoff before departure.
- Locks: the trip's schedule row `FOR UPDATE` first (as ReplaceTripFares, and every request that changes a
  schedule's trips), then the trip row `FOR UPDATE`.
- The trip's status, cause `Operator`, reason and cancellation time (`IClock`) are set together, and the
  TripCancelled event is written by the same statement (`WITH cancelled AS (UPDATE … RETURNING) INSERT`);
  the UPDATE only moves a `Scheduled` trip. The `Operator` cause blocks the date from regeneration by
  extension (CreateScheduleTrips) and edit (ReplaceSchedule).
- The reason is 10–500 characters as sent, not trimmed (`TripRules.IsAcceptableCancelReason`), matching the
  `cancel_reason` CHECK and the way bus names are measured.

## Known deviation
- Another operator's trip answers `404 NOT_FOUND`, per the coding standard (out-of-scope → 404).
  Decided by the developer on 2026-10-09 for every slice; drift reported upstream against AC-6.
- A `tripId` that is not a UUID answers `400 VALIDATION_FAILED` from model binding.
