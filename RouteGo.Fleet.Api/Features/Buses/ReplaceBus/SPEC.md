# CAP-BUS-004 — Replace bus

- Capability: CAP-BUS-004
- Canonical spec: `spec-RouteGo.Fleet.API/capabilities/buses/features/bus-management.md` (v0.1.0)
- Area: Buses
- Slice: `Features/Buses/ReplaceBus/`

## Contract
Implemented **verbatim** from the issued contract — if it looks wrong, report drift upstream
(`skills-spec-authoring`), never deviate locally.

- Operation: `replaceBus` — `contracts/buses/openapi.yaml` → `contracts/buses/bus.yaml`
- Route / verb: `PUT /buses/{busId}`
- Request: path `busId` (uuid); body `BusRequest` (full replacement — every field again)
- Response: `200` with `Bus`
- Problem codes: `VALIDATION_FAILED` (400), `UNAUTHORIZED` (401), `FORBIDDEN` (403, caller not an
  operator), `OPERATOR_PROFILE_NOT_READY` (403), `NOT_FOUND` (404),
  `BUS_REGISTRATION_ALREADY_EXISTS` (409), `BUS_IN_USE` (409, with `futureTripCount` and `nextDepartureAt`)

## Acceptance criteria
Each criterion maps to tests in `ReplaceBus.Tests.cs`.

- [x] AC-2: Registration number already in RouteGo (replace) → `ReplaceBus_RegistrationNumberOfAnotherBus_IsRefusedAndNothingChanges`
- [x] AC-3: Seat layout rules (replace) → `ReplaceBus_FieldOrLayoutBreaksARule_IsRefusedNamingTheFieldAndNothingChanges` (layout rows)
- [x] AC-4: Field rules (replace) → `ReplaceBus_FieldOrLayoutBreaksARule_IsRefusedNamingTheFieldAndNothingChanges` (field rows)
- [x] AC-8: Unknown bus (replace) → `ReplaceBus_UnknownBus_IsRefusedNotFound`
- [~] AC-9: Another operator's bus (replace) → `ReplaceBus_AnotherOperatorsBus_IsReportedNotFoundAndNothingChanges` —
  **deliberately 404, not the spec's FORBIDDEN** (see Known deviation)
- [x] AC-10: Free details change at any time → `ReplaceBus_FreeDetailsWhileInUse_AreUpdatedAndTripsLeftAlone`.
  "Trips generated afterwards carry the new name, AC and amenities" is tested by the trip-generating
  slices (CreateSchedule, CreateScheduleTrips), which read the bus when they generate:
  `CreateSchedule_BusDetailsChangedBefore_TripsCarryTheNewDetails`,
  `CreateScheduleTrips_BusRenamedBefore_NewTripsCarryTheNewName`.
- [x] AC-11: Locked details while in use → `ReplaceBus_LockedDetailChangedWhileInUse_IsRefusedBusInUseAndNothingChanges`
- [x] AC-12: Locked details change once unused → `ReplaceBus_NewLayoutWithOnlyDepartedAndCancelledTrips_IsReplaced`
- [x] AC-13: Profile not ready (replace) → `ReplaceBus_OperatorWithoutProfile_IsRefusedProfileNotReady`
- [x] AC-14: Only operators (replace) → `ReplaceBus_NotAnOperator_IsRefusedForbidden`
- [x] AC-15: Must be signed in (replace) → `ReplaceBus_WithoutAccessToken_IsRefusedUnauthorized`
- [x] Business rule 4 (equal layout in another case/order is unchanged) →
  `ReplaceBus_SameLayoutInOtherCaseAndOrderWhileInUse_IsAllowedAndKeepsStoredSpelling`

## Invariants touched
- Field and layout rules: `Domain/Buses/BusRules` (the validator delegates to it, as CreateBus's does).
- "Locked while in use" (buses shared rule 9): a future scheduled trip is a `Scheduled` trip whose
  departure is after the clock's now. The bus row is locked `FOR UPDATE` before the check.
- "Unchanged" (feature rule 4): `ReplaceBus.LockedDetails.cs`. When the seat classes and layout are
  unchanged under that rule, the stored rows are kept as they are — stored spelling and display order
  win over the request's — so trips already generated keep matching the bus's class names exactly
  (decided by the developer on 2026-10-10). When they change, the bus's seats, decks and classes are
  deleted and re-inserted in the same transaction.

## Known deviation
- Another operator's bus answers `404 NOT_FOUND`, indistinguishable from a missing bus, per
  the RouteGo coding standard's rule that out-of-scope resources return 404, never 403. Decided by
  the developer on 2026-10-09; drift reported upstream against AC-9 and the contract's 403 description.
- A `busId` that is not a UUID answers `400 VALIDATION_FAILED` from model binding.
