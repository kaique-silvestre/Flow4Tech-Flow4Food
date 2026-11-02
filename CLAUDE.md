# Flow4Tech: sistema de gestao

Modular monolith. .NET backend, two React front apps (`app`, `control`) plus a shared design-system package. Planning docs: `.scratch/novo-sistema/`; current spec: `.scratch/fundacao-tecnica/spec.md`.

## Structure
- `backend/Flow4Tech.sln`
  - `src/Kernel`: small stable shared code (organization/unit context, `Money`, `EntityId`, `IClock`; later decision engine contract, event bus, audit).
  - `src/<Module>/{Domain,Application,Infrastructure,Interface}`: one folder per module (`Organizacao`, `Permissoes`). Project names `Flow4Tech.<Module>.<Layer>`.
  - `tests/Flow4Tech.Tests`: xUnit.
- `frontend/{app,control,design-system}`: pnpm workspaces (listed in root `pnpm-workspace.yaml`); `frontend/tsconfig.base.json` is strict.
- `docs/adr/`: decisions; copy `0000-template.md`.

## Commands (run from repo root)
- build: `dotnet build backend/Flow4Tech.sln`
- test: `dotnet test backend/Flow4Tech.sln` (frontend has no tests yet)
- typecheck: `dotnet build backend/Flow4Tech.sln` (nullable + strict analyzers, warnings are errors) and `pnpm install && pnpm -r typecheck`
- lint/format: `dotnet format backend/Flow4Tech.sln` (add `--verify-no-changes` to check only)
- dev / port: not defined yet (no runnable app until the API host exists).

## Module-boundary rules
- Layers: Domain -> Kernel only; Application -> Domain; Infrastructure and Interface -> Application. Never the reverse.
- A module may use another module only through its public API (Application contracts) or its events. Never reference another module's Domain, Infrastructure or Interface.
- Kernel stays small; do not put business rules in it.

## Conventions
- Money: integer cents plus currency (`Money`). Never float/decimal for stored amounts.
- Time: UTC only, via injected `IClock`; do not call `DateTime.UtcNow`/`DateTimeOffset.UtcNow` in domain code.
- Ids: UUID v7 (`EntityId.New()`).
- Data: plain Postgres, explicit SQL, no ORM hiding SQL. Config by environment variables; standard container.
- Nullable reference types on; analyzers at `AnalysisMode=All`; warnings are errors. Do not suppress rules to pass.

## Writing a route the secure way
1. Resolve the organization/unit from `OrganizationContext` (authenticated session), never from client input.
2. Authorize explicitly with a named permission before any work; deny by default.
3. Validate input at the Interface layer; pass typed commands to Application.
4. Scope every query by organization (and unit when applicable).
5. Audit state-changing actions; return no data the caller's permission does not cover.
6. Cover the route with a test for the allowed case and for the denied/cross-organization case.
(The concrete mechanisms land in later tickets; keep this list in sync.)
