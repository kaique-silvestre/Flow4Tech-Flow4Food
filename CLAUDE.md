# Flow4Tech: sistema de gestao

Modular monolith. .NET backend, two React front apps (`app`, `control`) plus a shared design-system package. Planning docs: `.scratch/novo-sistema/`; current spec: `.scratch/fundacao-tecnica/spec.md`.

## Structure
- `backend/Flow4Tech.sln`
  - `src/Kernel`: small stable shared code (organization/unit context, `Money`, `EntityId`, `IClock`; later decision engine contract, event bus, audit).
  - `src/<Module>/{Domain,Application,Infrastructure,Interface}`: one folder per module (`Organizacao`, `Permissoes`). Project names `Flow4Tech.<Module>.<Layer>`.
  - `src/Infrastructure/Data`: shared technical data access (`Flow4Tech.Infrastructure.Data`: `TenantDatabase`, `MigrationRunner`); not a module, modules do not reference it directly.
  - `src/Api`: host (`Flow4Tech.Api`, Web SDK); health endpoints.
  - `tests/Flow4Tech.Tests`: xUnit. `tests/Isolation`: tenant isolation + RLS architecture tests against real PostgreSQL (classes named `*Integration*`).
  - `backend/migrations`: versioned plain SQL (`NNNN_name.sql`), see Data and tenant isolation.
- `frontend/{app,control,design-system}` and `frontend/packages/api-client` (generated TypeScript client, imported by `app` and `control`): pnpm workspaces (listed in root `pnpm-workspace.yaml`); `frontend/tsconfig.base.json` is strict.
- `docs/adr/`: decisions; copy `0000-template.md`.

## Commands (run from repo root)
- build: `dotnet build backend/Flow4Tech.sln`
- test: `dotnet test backend/Flow4Tech.sln` (needs Docker running: integration tests start PostgreSQL via Testcontainers; frontend has no tests yet)
- typecheck: `dotnet build backend/Flow4Tech.sln` (nullable + strict analyzers, warnings are errors) and `pnpm install && pnpm -r typecheck`
- lint/format: `dotnet format backend/Flow4Tech.sln` (add `--verify-no-changes` to check only)
- dev: `docker compose up --build` (PostgreSQL + API; `docker compose down` to stop)
- port: API `http://localhost:8080` (`GET /health/live`, `GET /health/ready`), PostgreSQL `localhost:5432` (user/password/db `flow4tech`)
- contract: after changing any API route/DTO, regenerate and commit both outputs: `dotnet build backend/src/Api/Flow4Tech.Api.csproj -p:GenerateContract=true` (writes `backend/src/Api/openapi.json`) then `pnpm --filter @flow4tech/api-client generate` (writes `frontend/packages/api-client/src/schema.ts`, never edit by hand). Give every route a stable `.WithName("...")` (becomes the operationId) and typed responses (`TypedResults`/`.Produces<T>()`).
- client tests: `pnpm -r --if-present test`
- config: environment variables only, e.g. `ConnectionStrings__Default`.

## CI gates (`.github/workflows/ci.yml`, GitHub Actions)
One pipeline on every pull request and on push to `main`. Merge requires the aggregate job `gates` to be green (branch protection); `deploy` runs only on push to `main` after `gates`.
- `build`: `dotnet build -c Release` + `dotnet format --verify-no-changes`
- `typecheck-frontend`: `pnpm install --frozen-lockfile && pnpm -r typecheck`
- `unit-tests`: `Flow4Tech.Tests` excluding classes whose name contains `Endpoints` or `Integration`
- `integration-tests`: those classes, with real PostgreSQL (Testcontainers `postgres:17-alpine`). Name new integration test classes `*Endpoints*` or `*Integration*` or they run as unit tests.
- `architecture-tests`: `backend/tests/Architecture`
- `secret-scan`: gitleaks over full git history (`fetch-depth: 0`)
- `dependency-audit`: `dotnet list package --vulnerable --include-transitive` and `pnpm audit --audit-level=high`
- `openapi-contract`: regenerates `openapi.json` + client and fails if the committed output differs (`git diff --exit-code`); runs client tests; runs `oasdiff breaking --fail-on ERR` (pinned `tufin/oasdiff` image) between the base revision (PR base / push `before`) and the head contract, so removing or narrowing anything clients rely on fails the PR.
- `isolation-tests`: `backend/tests/Isolation` (tenant isolation + RLS architecture rule, real PostgreSQL via Testcontainers, app credential). Own project, so the `unit-tests`/`integration-tests` name filters (which target `Flow4Tech.Tests` only) neither run nor miss it.
- Later tickets add: permission gates (10).
- No secrets in the repo: config via environment variables; gitleaks fails the pipeline on any finding in history.

Duration budget (TARGET, not yet measured; replace with the first real run): whole pipeline <= 10 min wall clock, each job <= 5 min (integration <= 8 min). Local reference: build ~10 s, integration tests ~20 s with warm Docker.

## Module-boundary rules
- Layers: Domain -> Kernel only; Application -> Domain; Infrastructure and Interface -> Application. Never the reverse.
- A module may use another module only through its public API (Application contracts) or its events. Never reference another module's Domain, Infrastructure or Interface.
- Kernel stays small; do not put business rules in it.
- Enforced by `backend/tests/Architecture` (runs in `dotnet test`): forbidden edges, cycles, another module's internals (csproj refs and `using`), and Domain referencing packages, frameworks, or outer layers all fail the build.
- Declare a new allowed edge: add the target to the source module's list in `backend/modules.dependencies.json` (`"Permissoes": ["Organizacao"]` = Permissoes may reference `Organizacao.Application`; this file is the only source of truth). Then add the `ProjectReference` to `Flow4Tech.Organizacao.Application` from Permissoes' Application/Infrastructure/Interface project (never Domain). Cycles stay forbidden; a new module must be added to the file too.

## Conventions
- Money: integer cents plus currency (`Money`). Never float/decimal for stored amounts.
- Time: UTC only, via injected `IClock`; do not call `DateTime.UtcNow`/`DateTimeOffset.UtcNow` in domain code.
- Ids: UUID v7 (`EntityId.New()`).
- Data: plain Postgres, explicit SQL, no ORM hiding SQL. Config by environment variables; standard container.
- Nullable reference types on; analyzers at `AnalysisMode=All`; warnings are errors. Do not suppress rules to pass.

## Data and tenant isolation (ADR-002)
- Migrations: plain SQL files in `backend/migrations`, named `NNNN_name.sql`, applied in name order, one transaction per file, by `MigrationRunner.ApplyAsync(ownerConnectionString, dir)` with the OWNER credential (needs CREATEROLE). Never edit an applied file; add a new one. The app credential never runs migrations.
- Two credentials: owner/migration (owns tables) and `flow4tech_app` (created NOLOGIN by migration 0001; ops add LOGIN + password out of band; not owner, no BYPASSRLS, no DDL). The running app and all integration tests use the app credential.
- Writing a tenant table (copy `amostra_itens` in `0001_isolamento_base.sql`): `organizacao_id uuid NOT NULL REFERENCES organizacoes(id)`; `ENABLE` and `FORCE ROW LEVEL SECURITY`; policy `USING` and `WITH CHECK` on `organizacao_id = (SELECT app_organizacao_id())`; child references use composite FKs `(organizacao_id, parent_id) -> parent(organizacao_id, id)` (parent needs `UNIQUE (organizacao_id, id)`). The app role gets DML through default privileges; do not grant more. Only exceptions: `organizacoes` (tenant key is `id`) and `schema_migrations`, listed in `RlsArchitectureIntegrationTests`.
- Context API: all tenant data access goes through `TenantDatabase.BeginAsync(OrganizationContext)` (Infrastructure.Data), which opens a transaction and runs `set_config('app.organizacao_id', id, true)` (SET LOCAL semantics: dies with the transaction, never per connection). Use `tx.Connection`/`tx.Transaction`, call `CommitAsync`. Background jobs and event handlers open their own `BeginAsync` with the job's/event's organization. No context means zero rows and refused writes. Never build a connection that skips it.
- `FORCE` also binds the owner: seeds/backfills run inside a tenant context.
- `RlsArchitectureIntegrationTests` migrates a real database and fails the build for any table without the tenant column, enabled+forced RLS, or a policy on `app_organizacao_id()`. Adding a table needs no test edit. The accepted policy shape is exactly one policy FOR ALL TO PUBLIC with USING and WITH CHECK equal to the isolation expression; extra or different policies fail.
- Open items (ADR-002): ticket 07 decides how to enforce that modules reach tenant data only via `TenantDatabase` (the Data project is outside the module rules); ticket 08 defines context restore in jobs/handlers.

## Writing a route the secure way
1. Resolve the organization/unit from `OrganizationContext` (authenticated session), never from client input.
2. Authorize explicitly with a named permission before any work; deny by default.
3. Validate input at the Interface layer; pass typed commands to Application.
4. Scope every query by organization (and unit when applicable).
5. Audit state-changing actions; return no data the caller's permission does not cover.
6. Cover the route with a test for the allowed case and for the denied/cross-organization case.
(The concrete mechanisms land in later tickets; keep this list in sync.)
