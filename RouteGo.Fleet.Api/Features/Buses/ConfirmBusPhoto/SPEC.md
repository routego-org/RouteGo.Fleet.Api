# CAP-BUS-008 — Confirm bus photo

- Capability: CAP-BUS-008
- Canonical spec: `spec-RouteGo.Fleet.API/capabilities/buses/features/bus-photos.md` (v0.1.0)
- Area: Buses
- Slice: `Features/Buses/ConfirmBusPhoto/`

## Contract
Implemented **verbatim** from the issued contract — if it looks wrong, report drift upstream
(`skills-spec-authoring`), never deviate locally.

- Operation: `confirmBusPhoto` — `contracts/buses/openapi.yaml` → `contracts/buses/bus-photo.yaml`
- Route / verb: `POST /buses/{busId}/photos/{photoId}/confirm`
- Request: path `busId`, `photoId` (uuid); no body
- Response: `200` with `BusPhoto`
- Problem codes: `UNAUTHORIZED` (401), `FORBIDDEN` (403, caller not an operator),
  `OPERATOR_PROFILE_NOT_READY` (403), `NOT_FOUND` (404), `PHOTO_UPLOAD_EXPIRED` (422, with
  `expiredAt`), `PHOTO_NOT_UPLOADED` (422), `PHOTO_INVALID` (422, with `reason`)

## Acceptance criteria
Each criterion maps to tests in `ConfirmBusPhoto.Tests.cs`.

- [~] AC-4: Confirm a good upload → `ConfirmBusPhoto_GoodJpegOnBusWithTwoPhotos_BecomesThirdUploadedPhoto`.
  "The media store's own address refuses direct access" is the bucket's policy, set by deployment,
  not by this code (LocalStack does not enforce it).
- [x] AC-5: Confirm after the slot expired → `ConfirmBusPhoto_SlotExpiredAMinuteAgo_IsRefusedAndSlotAndFileDiscarded`
- [x] AC-6: Confirm before the file arrived → `ConfirmBusPhoto_FileNotSentYet_IsRefusedAndSlotStaysPending`
- [x] AC-7: The stored file breaks the rules → `ConfirmBusPhoto_StoredFileBreaksTheRules_IsRefusedAndSlotAndFileDiscarded`
- [x] AC-8: Confirming twice changes nothing → `ConfirmBusPhoto_AlreadyUploaded_ReturnsPhotoUnchanged`
- [x] AC-12: Unknown photo (confirm) → `ConfirmBusPhoto_UnknownPhoto_IsRefusedNotFound`
- [~] AC-13: Another operator's bus (confirm) → `ConfirmBusPhoto_PhotoOfAnotherOperatorsBus_IsReportedNotFound` —
  **deliberately 404, not the spec's FORBIDDEN** (see Known deviation)
- [x] AC-14: Profile not ready (confirm) → `ConfirmBusPhoto_OperatorWithoutProfile_IsRefusedProfileNotReady`
- [x] AC-15: Only operators, signed in (confirm) → `ConfirmBusPhoto_NotAnOperator_IsRefusedForbidden`, `ConfirmBusPhoto_WithoutAccessToken_IsRefusedUnauthorized`

## Invariants touched
- `Domain/Buses/BusPhotoRules.FindFault` (rule 5): larger than 3 MB → `tooLarge`; first bytes are
  the other format's signature → `formatMismatch`; neither JPEG nor PNG signature →
  `unsupportedFormat`. Judged by size and signature only, not a full decode (decided by the
  developer on 2026-10-10).
- A confirmed photo takes the next position (rule 7). The bus and photo rows are locked `FOR UPDATE`
  so two confirmations on one bus never take the same position.
- A discarded slot's file is removed from the media store before the row is deleted, so a failed
  removal leaves the slot in place to retry.

## Known deviation
- A photo of another operator's bus answers `404 NOT_FOUND`, per the RouteGo coding standard's rule
  that out-of-scope resources return 404, never 403. Decided by the developer on 2026-10-09; drift
  reported upstream against AC-13 and the contract's 403 description.
- An identifier that is not a UUID answers `400 VALIDATION_FAILED` from model binding.
