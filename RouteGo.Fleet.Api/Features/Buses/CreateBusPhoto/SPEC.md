# CAP-BUS-007 — Create bus photo (issue an upload slot)

- Capability: CAP-BUS-007
- Canonical spec: `spec-RouteGo.Fleet.API/capabilities/buses/features/bus-photos.md` (v0.1.0)
- Area: Buses
- Slice: `Features/Buses/CreateBusPhoto/`

## Contract
Implemented **verbatim** from the issued contract — if it looks wrong, report drift upstream
(`skills-spec-authoring`), never deviate locally.

- Operation: `createBusPhoto` — `contracts/buses/openapi.yaml` → `contracts/buses/bus-photo.yaml`
- Route / verb: `POST /buses/{busId}/photos`
- Request: path `busId` (uuid); body `BusPhotoUploadRequest`
- Response: `201` with `BusPhotoUploadSlot` (no Location header — the contract defines none)
- Problem codes: `VALIDATION_FAILED` (400), `UNAUTHORIZED` (401), `FORBIDDEN` (403, caller not an
  operator), `OPERATOR_PROFILE_NOT_READY` (403), `NOT_FOUND` (404), `PHOTO_LIMIT_REACHED` (422, with `maxPhotos`)

## Acceptance criteria
Each criterion maps to tests in `CreateBusPhoto.Tests.cs`.

- [x] AC-1: Get an upload slot → `CreateBusPhoto_BusWithTwoPhotos_IssuesASlotTheBrowserCanUploadTo`
  (also PUTs a file through the issued address to the LocalStack media store)
- [x] AC-2: Five photos at most → `CreateBusPhoto_FourPhotosAndAnOpenSlot_IsRefusedPhotoLimitReached`,
  `CreateBusPhoto_FourPhotosAndAnExpiredSlot_IssuesASlot`
- [x] AC-3: Only JPEG or PNG, at most 3 MB → `CreateBusPhoto_FormatOrSizeBreaksARule_IsRefusedNamingTheField`
- [x] AC-12: Unknown bus (slot) → `CreateBusPhoto_UnknownBus_IsRefusedNotFound`
- [~] AC-13: Another operator's bus (slot) → `CreateBusPhoto_AnotherOperatorsBus_IsReportedNotFound` —
  **deliberately 404, not the spec's FORBIDDEN** (see Known deviation)
- [x] AC-14: Profile not ready (slot) → `CreateBusPhoto_OperatorWithoutProfile_IsRefusedProfileNotReady`
- [x] AC-15: Only operators, signed in (slot) → `CreateBusPhoto_NotAnOperator_IsRefusedForbidden`, `CreateBusPhoto_WithoutAccessToken_IsRefusedUnauthorized`

## Invariants touched
- `Domain/Buses/BusPhotoRules`: size limit (3 MB), photos per bus (5), slot lifetime (5 minutes),
  content type per format, and the file location `buses/{busId}/photos/{photoId}` (rules 2–4).
  Kept as named constants, not configuration (decided by the developer on 2026-10-10).
- The limit counts uploaded photos and slots whose expiry is after the clock's now; the bus row is
  locked `FOR UPDATE` so two concurrent requests cannot both pass it.
- Media store: `Common/Ports/IMediaStore` → `Infrastructure/S3MediaStore` (presigned S3 PUT that
  signs the content type). Config `MediaStoreBucket` (Parameter Store
  `/routego/prod/fleet/media-store-bucket`); `AwsServiceUrl` only on a developer machine (LocalStack).
- Deployment prerequisites outside this code: the bucket blocks public access, allows browser PUTs
  only from the RouteGo Portal's origin (CORS), and is readable only by the CDN.

## Known deviation
- Another operator's bus answers `404 NOT_FOUND`, indistinguishable from a missing bus, per
  the RouteGo coding standard's rule that out-of-scope resources return 404, never 403. Decided by
  the developer on 2026-10-09; drift reported upstream against AC-13 and the contract's 403 description.
- A `busId` that is not a UUID answers `400 VALIDATION_FAILED` from model binding.
