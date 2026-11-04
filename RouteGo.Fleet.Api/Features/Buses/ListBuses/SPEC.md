# CAP-BUS-002 — List buses

- Capability: CAP-BUS-002
- Canonical spec: `spec-RouteGo.Fleet.API/capabilities/buses/features/bus-management.md` (v0.1.0)
- Area: Buses
- Slice: `Features/Buses/ListBuses/`

## Contract
Implemented **verbatim** from the issued contract — if it looks wrong, report drift upstream
(`skills-spec-authoring`), never deviate locally.

- Operation: `listBuses` — `contracts/buses/openapi.yaml` → `contracts/buses/bus.yaml`
- Route / verb: `GET /buses`
- Request: query `status` (`BusStatus`, optional), `page` (≥ 1, default 1), `pageSize` (1–200, default 20)
- Response: `200` with `BusListResponse` (`items` of `BusSummary`, `totalCount`, `page`, `pageSize`)
- Problem codes: `VALIDATION_FAILED` (400), `UNAUTHORIZED` (401), `FORBIDDEN` (403, caller not an
  operator), `OPERATOR_PROFILE_NOT_READY` (403)

## Acceptance criteria
Each criterion maps to tests in `ListBuses.Tests.cs`.

- [x] AC-5: List own buses → `ListBuses_ActiveAndInactiveOwnBuses_ReturnsBothByNameButNotAnotherOperatorsBus`,
  `ListBuses_StatusInactive_ReturnsOnlyTheInactiveBus`,
  `ListBuses_UploadedPhotosAndAPendingSlot_CoverIsTheFirstUploadedPhotoOnTheCdn` (cover photo address)
- [x] AC-6: No buses yet → `ListBuses_NoBusesYet_ReturnsAnEmptyPageWithTotalCountZero`
- [x] AC-13: Profile not ready (list) → `ListBuses_OperatorWithoutProfile_IsRefusedProfileNotReady`
- [x] AC-14: Only operators (list) → `ListBuses_NotAnOperator_IsRefusedForbidden`
- [x] AC-15: Must be signed in (list) → `ListBuses_WithoutAccessToken_IsRefusedUnauthorized`
- [x] Business rule 5 (sort by name ignoring case, paging) → `ListBuses_PagesOfTwo_AreSortedByNameIgnoringCase`
- [x] Query validation (contract's 400) → `ListBuses_InvalidQuery_IsRefusedValidationFailedNamingTheField`

## Invariants touched
- Paging limits and defaults: `Common/Paging.cs` (out-of-range → `VALIDATION_FAILED`).
- Cover photo = the uploaded photo at position 1 (bus-photos rule 7); its address is the CDN base
  (`CdnBaseUrl`, `Common/PhotoAddresses.cs`) followed by its file location (bus-photos rule 6).
- Ownership is the query's `WHERE operator_id = @OperatorId`.

## Notes
- This slice introduced `buses.bus_photos` (migration V0012) and the `CdnBaseUrl` setting
  (Parameter Store `/routego/prod/fleet/cdn-base-url`), ahead of the photo slices that write them.
- `status` must be spelled exactly as the contract's enum (`active`, `inactive`).
