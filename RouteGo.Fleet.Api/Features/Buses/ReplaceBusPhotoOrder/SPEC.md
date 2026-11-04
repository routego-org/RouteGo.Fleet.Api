# CAP-BUS-010 — Replace bus photo order

- Capability: CAP-BUS-010
- Canonical spec: `spec-RouteGo.Fleet.API/capabilities/buses/features/bus-photos.md` (v0.1.0)
- Area: Buses
- Slice: `Features/Buses/ReplaceBusPhotoOrder/`

## Contract
Implemented **verbatim** from the issued contract — if it looks wrong, report drift upstream
(`skills-spec-authoring`), never deviate locally.

- Operation: `replaceBusPhotoOrder` — `contracts/buses/openapi.yaml` → `contracts/buses/bus-photo.yaml`
- Route / verb: `PUT /buses/{busId}/photoOrder`
- Request: path `busId` (uuid); body `BusPhotoOrder`
- Response: `200` with `BusPhotoList`
- Problem codes: `VALIDATION_FAILED` (400), `UNAUTHORIZED` (401), `FORBIDDEN` (403, caller not an
  operator), `OPERATOR_PROFILE_NOT_READY` (403), `NOT_FOUND` (404)

## Acceptance criteria
Each criterion maps to tests in `ReplaceBusPhotoOrder.Tests.cs`.

- [x] AC-10: Choose the cover photo → `ReplaceBusPhotoOrder_ThirdPhotoFirst_ReturnsTheNewOrderWithItAsCover`
- [x] AC-11: The order must name exactly the uploaded photos → `ReplaceBusPhotoOrder_ListIsNotExactlyTheUploadedPhotos_IsRefusedAndOrderUnchanged`
- [x] AC-12: Unknown bus (order) → `ReplaceBusPhotoOrder_UnknownBus_IsRefusedNotFound`
- [~] AC-13: Another operator's bus (order) → `ReplaceBusPhotoOrder_AnotherOperatorsBus_IsReportedNotFoundAndOrderUnchanged` —
  **deliberately 404, not the spec's FORBIDDEN** (see Known deviation)
- [x] AC-14: Profile not ready (order) → `ReplaceBusPhotoOrder_OperatorWithoutProfile_IsRefusedProfileNotReady`
- [x] AC-15: Only operators, signed in (order) → `ReplaceBusPhotoOrder_NotAnOperator_IsRefusedForbidden`, `ReplaceBusPhotoOrder_WithoutAccessToken_IsRefusedUnauthorized`

## Invariants touched
- Rule 7: the order lists every uploaded photo exactly once. The validator refuses an empty,
  over-long or repeating list; the handler compares the list with the bus's uploaded photos and
  reports a mismatch as the same `VALIDATION_FAILED` on `photoIds` (it needs the database, but to the
  caller it is the same field error). The bus row is locked `FOR UPDATE` while the order is replaced.

## Known deviation
- Another operator's bus answers `404 NOT_FOUND`, per the RouteGo coding standard's rule that
  out-of-scope resources return 404, never 403. Decided by the developer on 2026-10-09; drift
  reported upstream against AC-13 and the contract's 403 description.
- A `busId` that is not a UUID answers `400 VALIDATION_FAILED` from model binding.
