# CAP-BUS-003 — Get bus

- Capability: CAP-BUS-003
- Canonical spec: `spec-RouteGo.Fleet.API/capabilities/buses/features/bus-management.md` (v0.1.0)
- Area: Buses
- Slice: `Features/Buses/GetBus/`

## Contract
Implemented **verbatim** from the issued contract — if it looks wrong, report drift upstream
(`skills-spec-authoring`), never deviate locally.

- Operation: `getBus` — `contracts/buses/openapi.yaml` → `contracts/buses/bus.yaml`
- Route / verb: `GET /buses/{busId}`
- Request: path `busId` (uuid)
- Response: `200` with `Bus` (photos: `contracts/buses/bus-photo.yaml` → `BusPhoto`)
- Problem codes: `UNAUTHORIZED` (401), `FORBIDDEN` (403, caller not an operator),
  `OPERATOR_PROFILE_NOT_READY` (403), `NOT_FOUND` (404)

## Acceptance criteria
Each criterion maps to tests in `GetBus.Tests.cs`.

- [x] AC-7: Read one bus → `GetBus_OwnBusWithTwoUploadedPhotos_ReturnsEveryDetailAndThePhotosInOrder`
- [x] AC-8: Unknown bus (read) → `GetBus_UnknownBus_IsRefusedNotFound`
- [~] AC-9: Another operator's bus (read) → `GetBus_AnotherOperatorsBus_IsReportedNotFound` —
  **deliberately 404, not the spec's FORBIDDEN** (see Known deviation)
- [x] AC-13: Profile not ready (read) → `GetBus_OperatorWithoutProfile_IsRefusedProfileNotReady`
- [x] AC-14: Only operators (read) → `GetBus_NotAnOperator_IsRefusedForbidden`
- [x] AC-15: Must be signed in (read) → `GetBus_WithoutAccessToken_IsRefusedUnauthorized`

## Invariants touched
- None in `Domain/`; ownership is in the lookup's `WHERE` clause.
- `photos` lists only uploaded photos, by position (bus-photos rule 7); each `url` is the CDN base
  followed by the file location (`Common/PhotoAddresses.cs`, bus-photos rule 6).

## Known deviation
- Another operator's bus answers `404 NOT_FOUND`, indistinguishable from a missing bus, per the
  RouteGo coding standard's rule that out-of-scope resources return 404, never 403. Decided by the
  developer on 2026-10-09; drift reported upstream against AC-9 and the contract's 403 description.
- A `busId` that is not a UUID answers `400 VALIDATION_FAILED` (the contract lists no 400 for this
  operation; model binding refuses the value before the slice runs).
- The seat's `column` is read from the `seat_column` column (reserved SQL word; see CreateBus's drift).
