# CAP-BUS-009 — Delete bus photo

- Capability: CAP-BUS-009
- Canonical spec: `spec-RouteGo.Fleet.API/capabilities/buses/features/bus-photos.md` (v0.1.0)
- Area: Buses
- Slice: `Features/Buses/DeleteBusPhoto/`

## Contract
Implemented **verbatim** from the issued contract — if it looks wrong, report drift upstream
(`skills-spec-authoring`), never deviate locally.

- Operation: `deleteBusPhoto` — `contracts/buses/openapi.yaml` → `contracts/buses/bus-photo.yaml`
- Route / verb: `DELETE /buses/{busId}/photos/{photoId}`
- Request: path `busId`, `photoId` (uuid); no body
- Response: `204`, no body
- Problem codes: `UNAUTHORIZED` (401), `FORBIDDEN` (403, caller not an operator),
  `OPERATOR_PROFILE_NOT_READY` (403), `NOT_FOUND` (404)

## Acceptance criteria
Each criterion maps to tests in `DeleteBusPhoto.Tests.cs`.

- [x] AC-9: Delete a photo → `DeleteBusPhoto_MiddleOfThreePhotos_RemovesItAndItsFileAndClosesTheGap`,
  `DeleteBusPhoto_PendingSlot_RemovesItAndLeavesTheOrderAlone`. "The bus can take another photo"
  follows from the row being gone: CreateBusPhoto counts only existing photos and open slots.
- [x] AC-12: Unknown photo (delete) → `DeleteBusPhoto_UnknownPhoto_IsRefusedNotFound`
- [~] AC-13: Another operator's bus (delete) → `DeleteBusPhoto_PhotoOfAnotherOperatorsBus_IsReportedNotFoundAndKept` —
  **deliberately 404, not the spec's FORBIDDEN** (see Known deviation)
- [x] AC-14: Profile not ready (delete) → `DeleteBusPhoto_OperatorWithoutProfile_IsRefusedProfileNotReady`
- [x] AC-15: Only operators, signed in (delete) → `DeleteBusPhoto_NotAnOperator_IsRefusedForbidden`, `DeleteBusPhoto_WithoutAccessToken_IsRefusedUnauthorized`

## Invariants touched
- Photo order has no gap (rule 7): the photos after the deleted one move up a place, inside the
  same transaction (the position uniqueness is deferred to commit).
- The stored file is removed from the media store before the row, so a failed removal keeps the
  photo for a retry.

## Known deviation
- A photo of another operator's bus answers `404 NOT_FOUND`, per the RouteGo coding standard's rule
  that out-of-scope resources return 404, never 403. Decided by the developer on 2026-10-09; drift
  reported upstream against AC-13 and the contract's 403 description.
- An identifier that is not a UUID answers `400 VALIDATION_FAILED` from model binding.
