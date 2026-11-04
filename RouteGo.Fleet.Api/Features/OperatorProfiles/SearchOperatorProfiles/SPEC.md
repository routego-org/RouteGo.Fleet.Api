# CAP-OPP-002 — Search operator profiles (Admin)

- Capability: CAP-OPP-002
- Canonical spec: `spec-RouteGo.Fleet.API/capabilities/operator-profiles/features/operator-profile-search.md` (v0.1.0)
- Area: OperatorProfiles
- Slice: `Features/OperatorProfiles/SearchOperatorProfiles/`

## Contract
Implemented **verbatim** from the issued contract — if it looks wrong, report drift upstream
(`skills-spec-authoring`), never deviate locally.

- Operation: `searchOperatorProfiles` — `contracts/operator-profiles/openapi.yaml` → `contracts/operator-profiles/operator-profile.yaml`
- Route / verb: `GET /admin/operatorProfiles`
- Request: query `companyName` (1–150 characters), `page`, `pageSize`
- Response: `200` with `OperatorProfileListResponse` (`items` of `OperatorProfile`, `totalCount`, `page`, `pageSize`)
- Problem codes: `VALIDATION_FAILED` (400), `UNAUTHORIZED` (401), `FORBIDDEN` (403, caller not an admin)

## Acceptance criteria
Each criterion maps to tests in `SearchOperatorProfiles.Tests.cs`.

- [x] AC-1: Search by part of the company name → `SearchOperatorProfiles_ByPartOfTheCompanyName_ReturnsMatchesIgnoringCaseSortedByName`
- [x] AC-2: No filter lists every profile, paged → `SearchOperatorProfiles_FortyFiveProfilesPageThreeOfTwenty_ReturnsTheLastFiveWithTotal`
  (paging and total) and `SearchOperatorProfiles_NoCompanyName_ListsEveryProfileSortedByNameIgnoringCase` (no filter)
- [x] AC-3: No match is an empty page → `SearchOperatorProfiles_NoMatch_ReturnsAnEmptyPage`
- [x] AC-4: Filter too long → `SearchOperatorProfiles_FilterTooLongOrPageOutOfRange_IsRefusedNamingTheField`
- [x] AC-5: Only admins → `SearchOperatorProfiles_NotAnAdmin_IsRefusedForbidden`
- [x] AC-6: Must be signed in → `SearchOperatorProfiles_WithoutAccessToken_IsRefusedUnauthorized`

## Invariants touched
- Admin only: `Policies.Admin`. No operator scope and no profile gate — the capability emits neither.
- The filter is a contains-match ignoring case (`strpos` on `lower()`), so `%` and `_` match literally.
- Sorted by company name ignoring case, then operator id; paged with the shared paging parameters.
  Read only (reader connection). Profiles are written only by the OperatorApproved worker.

## Known deviation
- **AC-2 test shape**: the shared test database holds every test's profiles, so an unfiltered search
  never totals exactly 45. AC-2 is covered by two tests: 45 profiles sharing a unique marker, searched by
  that marker (page 3 of 20 → 5, total 45); and an unfiltered search whose total equals the table's row
  count and whose first page starts with this test's own profiles in case-insensitive order. Decided by the
  developer on 2026-10-11. The behaviour itself matches the spec.
