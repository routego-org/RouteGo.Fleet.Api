# CAP-BUS-006 — Activate bus

- Capability: CAP-BUS-006
- Canonical spec: `spec-RouteGo.Fleet.API/capabilities/buses/features/bus-activation.md` (v0.1.0)
- Area: Buses
- Slice: `Features/Buses/ActivateBus/`

## Contract
Implemented **verbatim** from the issued contract — if it looks wrong, report drift upstream
(`skills-spec-authoring`), never deviate locally.

- Operation: `activateBus` — `contracts/buses/openapi.yaml` → `contracts/buses/bus.yaml`
- Route / verb: `POST /buses/{busId}/activate`
- Request: path `busId` (uuid); no body
- Response: `200` with `Bus`
- Problem codes: `UNAUTHORIZED` (401), `FORBIDDEN` (403, caller not an operator),
  `OPERATOR_PROFILE_NOT_READY` (403), `NOT_FOUND` (404)

## Acceptance criteria
Each criterion maps to tests in `ActivateBus.Tests.cs`.

- [x] AC-3: Activate an inactive bus → `ActivateBus_InactiveBus_BecomesActive`. "Can be put on a
  schedule" is tested by CreateSchedule (CAP-TRP-001), which owns the active-bus check:
  `CreateSchedule_BusActivatedAgain_CanBeScheduled`.
- [x] AC-4: Repeating the same action changes nothing (activate) → `ActivateBus_AlreadyActive_ReturnsBusUnchanged`
- [x] AC-5: Unknown bus (activate) → `ActivateBus_UnknownBus_IsRefusedNotFound`
- [~] AC-6: Another operator's bus (activate) → `ActivateBus_AnotherOperatorsBus_IsReportedNotFoundAndLeftInactive` —
  **deliberately 404, not the spec's FORBIDDEN** (see Known deviation)
- [x] AC-7: Profile not ready (activate) → `ActivateBus_OperatorWithoutProfile_IsRefusedProfileNotReady`
- [x] AC-8: Only operators, signed in (activate) → `ActivateBus_NotAnOperator_IsRefusedForbidden`, `ActivateBus_WithoutAccessToken_IsRefusedUnauthorized`

## Invariants touched
- None in `Domain/`. Activation has no precondition; ownership and "only an inactive bus changes"
  are in the UPDATE's `WHERE` clause, so a repeat activation writes no audit entry.

## Known deviation
- Another operator's bus answers `404 NOT_FOUND`, indistinguishable from a missing bus, per
  the RouteGo coding standard's rule that out-of-scope resources return 404, never 403. Decided by
  the developer on 2026-10-09; drift reported upstream against AC-6 and the contract's 403 description.
- A `busId` that is not a UUID answers `400 VALIDATION_FAILED` (the contract lists no 400 for this
  operation; model binding refuses the value before the slice runs).
