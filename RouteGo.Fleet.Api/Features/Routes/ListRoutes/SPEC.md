# CAP-RTE-003 — List routes

- Capability: CAP-RTE-003
- Canonical spec: `spec-RouteGo.Fleet.API/capabilities/routes/features/route-management.md` (v0.1.0)
- Area: Routes
- Slice: `Features/Routes/ListRoutes/`

## Contract
Implemented **verbatim** from the issued contract — if it looks wrong, report drift upstream
(`skills-spec-authoring`), never deviate locally.

- Operation: `listRoutes` — `contracts/routes/openapi.yaml` → `contracts/routes/route.yaml`
- Route / verb: `GET /routes`
- Request: query `status` (`RouteStatus`, optional), `page` (≥ 1, default 1), `pageSize` (1–200, default 20)
- Response: `200` with `RouteListResponse` (`items`, `totalCount`, `page`, `pageSize`)
- Problem codes: `VALIDATION_FAILED` (400), `UNAUTHORIZED` (401), `FORBIDDEN` (403, caller not an
  operator), `OPERATOR_PROFILE_NOT_READY` (403)

## Acceptance criteria
Each criterion maps to tests in `ListRoutes.Tests.cs`.

- [x] AC-4: List own routes → `ListRoutes_ActiveAndInactiveOwnRoutes_ReturnsBothButNotAnotherOperatorsRoute`,
  `ListRoutes_StatusActive_ReturnsOnlyTheActiveRoute`
- [x] AC-5: No routes yet → `ListRoutes_NoRoutesYet_ReturnsAnEmptyPageWithTotalCountZero`
- [x] AC-11: Profile not ready (list) → `ListRoutes_OperatorWithoutProfile_IsRefusedProfileNotReady`
- [x] AC-12: Only operators, signed in (list) → `ListRoutes_NotAnOperator_IsRefusedForbidden`,
  `ListRoutes_WithoutAccessToken_IsRefusedUnauthorized`
- [x] Business rule 4 (sort and paging) → `ListRoutes_PagesOfTwo_AreSortedBySourceThenDestination`
- [x] Query validation (contract's 400) → `ListRoutes_InvalidQuery_IsRefusedValidationFailedNamingTheField`

## Invariants touched
- Paging limits and defaults are shared by every list slice: `Common/Paging.cs`. Out-of-range
  `page`/`pageSize` is refused with `VALIDATION_FAILED` as the contract says, not clamped.
- Ownership is the query's `WHERE operator_id = @OperatorId`; no `Domain/` rule is involved.

## Notes
- `status` must be spelled exactly as the contract's enum (`active`, `inactive`); `Active` or a
  number is refused.
- A non-integer `page` or `pageSize` is refused by model binding with the same `VALIDATION_FAILED` shape.
