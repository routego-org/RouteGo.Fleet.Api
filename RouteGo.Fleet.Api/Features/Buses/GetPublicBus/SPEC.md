# CAP-BUS-011 — Get public bus

- Capability: CAP-BUS-011
- Canonical spec: `spec-RouteGo.Fleet.API/capabilities/buses/features/public-bus-details.md` (v0.1.0)
- Area: Buses
- Slice: `Features/Buses/GetPublicBus/`

## Contract
Implemented **verbatim** from the issued contract — if it looks wrong, report drift upstream
(`skills-spec-authoring`), never deviate locally.

- Operation: `getPublicBus` — `contracts/buses/openapi.yaml` → `contracts/buses/bus.yaml`
- Route / verb: `GET /public/buses/{busId}` (anonymous — `security: []`)
- Request: path `busId` (uuid)
- Response: `200` with `PublicBus`
- Problem codes: `NOT_FOUND` (404)

## Acceptance criteria
Each criterion maps to tests in `GetPublicBus.Tests.cs`.

- [x] AC-1: Read a bus's public page → `GetPublicBus_WithoutAccessToken_ReturnsOnlyThePublicDetailsAndPhotosInOrder`
  (also asserts the exact set of returned fields, so nothing private can leak in)
- [x] AC-2: An inactive bus still shows → `GetPublicBus_InactiveBus_StillShows`
- [x] AC-3: A bus without photos → `GetPublicBus_NoUploadedPhoto_ReturnsAnEmptyPhotoList`
- [x] AC-4: Unknown bus → `GetPublicBus_UnknownBus_IsRefusedNotFound`
- [x] AC-5: A token is not needed, and does not change the answer → `GetPublicBus_WithAnyAccessToken_ReturnsTheSameAsWithout`

## Invariants touched
- None in `Domain/`. Only the fields the contract lists are selected; photos are the uploaded ones
  in photo order with CDN addresses (`Common/PhotoAddresses.cs`, bus-photos rule 6).
- `[AllowAnonymous]`: no profile gate and no ownership — the page is public for every bus.

## Known deviation
- A `busId` that is not a UUID answers `400 VALIDATION_FAILED` from model binding (the contract
  lists no 400 for this operation).
