# Flow4Tech: sistema de gestao

Modular monolith. .NET backend, two React front apps (`app`, `control`) plus a shared design-system package. Planning docs: `.scratch/S-00-novo-sistema/`; current spec: `.scratch/S-01-fundacao-tecnica/spec.md`.

## Structure
- `backend/Flow4Tech.sln`
  - `src/Kernel`: small stable shared code (organization/unit context, `Money`, `EntityId`, `IClock`; `Events/IDomainEvent`; `Authorization/IAuthorizationEngine`: the single decision interface, ADR-006; `Audit/`: `IAuditLog`, `AuditEntry`, `ITechnicalLog`, the secret rule `SensitiveData`, `Correlation`).
  - `src/<Module>/{Domain,Application,Infrastructure,Interface}`: one folder per module (`Organizacao`, `Permissoes`, `Identidade`). Project names `Flow4Tech.<Module>.<Layer>`.
  - `src/Infrastructure/Data`: shared technical data access (`Flow4Tech.Infrastructure.Data`: `TenantDatabase`, `MigrationRunner`); not a module; only a module's Infrastructure layer (and Api) may reference it.
  - `src/Infrastructure/Outbox`: transactional outbox and background-job seam (`Flow4Tech.Infrastructure.Outbox`: `OutboxWriter`, `OutboxDispatcher`, `OutboxSweeper`, `IOutboxConsumer`, `IdempotentConsumer`, `IBackgroundJobs`); not a module, built on `TenantDatabase`; module Infrastructure layers may reference it.
  - `tests/Authorization`: authorization engine contract (profile x permission matrix, cases 1-3, deny precedence), the factory policy and the cumulative-limit concurrency test against real PostgreSQL (classes named `*Integration*`).
  - `tests/Organizacao`: layered settings (ticket 10): pure resolution, option declaration and value rules, and the service against real PostgreSQL (inheritance, lock, history, restore, start instant, permission, audit, RLS, append-only; classes named `*Integration*`).
  - `src/Infrastructure/Audit`: audit trail and technical log (`Flow4Tech.Infrastructure.Audit`: `PostgresAuditLog`, `AuditChainVerifier`, `AuditJobs`, `TechnicalLog`); not a module, built on `TenantDatabase` and the outbox; registered by `AddAuditoria()`.
  - `src/Infrastructure/Migrator`: deploy-time console (`Flow4Tech.Migrator`, `dotnet Flow4Tech.Migrator.dll`): applies `backend/migrations` with the OWNER credential, gives `flow4tech_app` its login, verifies the app credential is restricted; Npgsql stays in Data (`DatabaseUrl`, `AppRole`). Never run by the web app.
  - `src/Api`: host (`Flow4Tech.Api`, Web SDK); health endpoints; binds to `PORT` (8080 fallback), maps `DATABASE_URL`.
  - `tests/Flow4Tech.Tests`: xUnit. `tests/Isolation`: tenant isolation + RLS architecture tests against real PostgreSQL (classes named `*Integration*`). `tests/Outbox`: outbox, dispatcher, idempotent consumer and job-context tests against real PostgreSQL. `tests/Audit`: audit trail, chain verification, concurrency, secret rule and technical log against real PostgreSQL. `DoisTenantsEndpointsTests` (in `Flow4Tech.Tests`): two organizations through the real authenticated route (login, units, sessions, settings, profiles, invitations, accounts, audit, table sweep).
  - `backend/migrations`: versioned plain SQL (`NNNN_name.sql`), see Data and tenant isolation.
- `frontend/{app,control,design-system}` and `frontend/packages/api-client` (generated TypeScript client, imported by `app` and `control`): pnpm workspaces (listed in root `pnpm-workspace.yaml`); `frontend/tsconfig.base.json` is strict.
- `docs/tickets.md`: índice de tickets `F4-NNNN` (ID, spec, ticket, branch, PR, status); regras de branch/commit/PR em "Branches, IDs e PRs" abaixo e no ADR-016.
- `docs/adr/`: decisions; copy `0000-template.md`. Platform base: 002 RLS, 003 outbox, 005 identity, 006 authorization (incl. persisted rules), 009 hosting, 010 sessions, 011 login protection, 012 invitations/recovery/Google, 013 layered settings.

## Commands (run from repo root)
- build: `dotnet build backend/Flow4Tech.sln`
- test: `dotnet test backend/Flow4Tech.sln` (needs Docker running: integration tests start PostgreSQL via Testcontainers; front tests are `pnpm -r --if-present test`)
- test without Docker (cloud sessions): the integration fixtures (`Isolation/PostgresFixture.cs`, and the per-class `*EndpointsDatabase` fixtures in `Flow4Tech.Tests`) were swapped TEMPORARILY for a local PostgreSQL 16 (superuser, one fresh database per fixture; the cluster needs `GRANT flow4tech_app TO flow4tech_owner WITH ADMIN OPTION`), run one suite at a time with `-m:1`, and the swap was reverted, never committed. This proves the logic, not the Testcontainers path (`postgres:17-alpine`).
- typecheck: `dotnet build backend/Flow4Tech.sln` (nullable + strict analyzers, warnings are errors) and `pnpm install && pnpm -r typecheck`
- lint/format: `dotnet format backend/Flow4Tech.sln` (add `--verify-no-changes` to check only); front: `pnpm lint` (Biome, check only, same as CI) and `pnpm lint:fix` (applies format and safe fixes). Config `biome.jsonc` at the root, covers `frontend/**` except the generated `schema.ts`.
- dev: `docker compose up --build` (PostgreSQL + API; `docker compose down` to stop)
- port: API `http://localhost:8080` (`GET /health/live`, `GET /health/ready`), PostgreSQL `localhost:5432` (user/password/db `flow4tech`)
- front architecture: `pnpm arch:front` (dependency-cruiser, `.dependency-cruiser.cjs`; rules in `docs/agents/ci-gates.md`); dead code: `pnpm dead:front` (Knip)
- contract: after changing any API route/DTO, regenerate and commit both outputs: `dotnet build backend/src/Api/Flow4Tech.Api.csproj -p:GenerateContract=true` (writes `backend/src/Api/openapi.json`) then `pnpm --filter @flow4tech/api-client generate` (writes `frontend/packages/api-client/src/schema.ts`, never edit by hand). Give every route a stable `.WithName("...")` (becomes the operationId) and typed responses (`TypedResults`/`.Produces<T>()`).
- client tests: `pnpm -r --if-present test`
- config (variáveis de ambiente), backup (produção NÃO tem backup de banco; faça `pg_dump` manual antes de mudança arriscada) e deploy (Railway/Vercel): `docs/agents/project.md`, seção "Config, backup e deploy".

## Detalhe sob demanda (docs/agents/)
Leia o arquivo ANTES de mexer na área; a raiz guarda só o que vale para todo agente. Detalhe novo de módulo ou gate vai para o arquivo do tema (ou um novo em `docs/agents/`), nunca como bloco novo aqui: `ClaudeMdSizeTests` limita esta raiz.
- **Autorização, permissões de rota, perfis** (`DecidirAsync`, `.RequirePermission`, `GrantGuard`, unidade de trabalho com teto): `docs/agents/autorizacao.md`
- **Dados e isolamento por organização** (migrações SQL, RLS, `TenantDatabase`, EF Core, outbox): `docs/agents/dados-e-tenant.md`
- **Auditoria** (`IAuditLog`, cadeia de hash, `SensitiveData`, log técnico): `docs/agents/auditoria.md`
- **Identidade, sessões, login, convites, recuperação, Google**: `docs/agents/identidade.md`
- **Contexto da requisição e a ordem dos middlewares** (`RequestContextMiddleware`, rota de ponta a ponta): `docs/agents/contexto-e-rota.md`
- **Configurações em camadas** (`[SettingsCatalog]`, `ISettings`): `docs/agents/configuracoes.md`
- **Salão** (comandas, caixa, cardápio, mesas, pagamentos, front `/salao`): `docs/agents/salao.md`
- **CI e testes** (cada job, FsCheck, Stryker, axe, orçamento de bundle, cobertura): `docs/agents/ci-gates.md`
- Comandos do dia a dia, seed e2e: `docs/agents/project.md`; tracker: `docs/agents/issue-tracker.md`.

## Regras que valem em qualquer tarefa
- Dados de organização só passam por `TenantDatabase`/`IUnitOfWork`; a RLS filtra, o código nunca filtra por `organizacao_id`.
- Organização e unidade vêm só da sessão autenticada, nunca do cliente.
- Toda rota declara `.RequireSession()` e `.RequirePermission(...)`; permissão com limite é decidida no handler de Application com valores do servidor.
- Toda ação que muda estado grava `IAuditLog` na mesma unidade de trabalho.
- Código nunca ramifica por nome de perfil; a decisão é do `IAuthorizationEngine`.
- Migração aplicada nunca é editada; crie um arquivo novo. Segredos só por variável de ambiente.
- Warnings são erros; corrija a causa em vez de suprimir a regra.

## CI gates (resumo)
Um pipeline em todo PR e em todo push em `dev-v2`/`main-v2`/`main`; o merge exige o job agregado `gates` verde. Bloqueiam: build + `dotnet format`, typecheck, lint (Biome), testes unitários, de integração (PostgreSQL real), de arquitetura (back e front, `architecture-frontend`, `dead-code-frontend`) e por módulo, `frontend-tests` (piso de cobertura), `bundle-budget`, `mutation-tests` (piso por domínio), `frontend-e2e` (inclui axe), gitleaks, trivy, `openapi-contract`, auditoria de dependências. Informativos: cobertura do back, DAST. Cada job, limite e comando local: `docs/agents/ci-gates.md`.

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
- Data: plain Postgres, EF Core with SQL kept visible and testable (schema and RLS are SQL migrations; see Data and tenant isolation). Config by environment variables; standard container.
- Nullable reference types on; analyzers at `AnalysisMode=All` plus SecurityCodeScan and Roslynator (`backend/Directory.Build.props`, versions pinned; Roslynator only has `RCS1112` and `RCS1186` as warnings, the rest of its catalog was measured and left off, reasons in `backend/.editorconfig`; F4-0101); warnings are errors. Do not suppress rules to pass. `dotnet format --verify-no-changes` also reports analyzer errors (it caught `RCS1139`): run it, not only the build.

## Branches, IDs e PRs (ADR-016) — obrigatório para todo agente
- **Todo trabalho tem um ID de ticket `F4-NNNN`** (sequencial, único, nunca reaproveitado), registrado em `docs/tickets.md` ANTES de começar: acrescente a linha com o próximo número livre (título, fatia, spec, arquivo do ticket). Sem ID não há branch, commit nem PR. Manutenção também abre ticket.
- **Branches de longa vida do sistema novo:** `main-v2` (revisado, publicável) e `dev-v2` (integração). `main-v2` nunca recebe commit nem push direto, só PR vindo de `dev-v2`. `dev-v2` aceita commit e push direto (o ID `F4-NNNN` continua obrigatório no commit); branch de ticket + PR segue valendo para trabalho maior ou quando o dono pedir. `main`, `staging`, `development` e `refactor-v2` são o sistema antigo e o planejamento: não tocar.
- **Branch de trabalho:** `<tipo>/F4-NNNN-<resumo-em-kebab>`, criada a partir de `dev-v2`; tipos `feat|fix|refactor|test|docs|chore`; exemplo `feat/F4-0013-fila-offline`. Uma branch por ticket, vida curta. Se o ambiente impuser outro nome de branch (sessão de nuvem `claude/...`), use-o, mas o ID continua obrigatório nos commits e no PR.
- **Commit:** Conventional Commits em inglês com o ID como escopo: `feat(F4-0013): offline pending-actions queue`. Corpo curto dizendo o que foi provado. Nunca reescrever commits já publicados.
- **PR:** só quando o dono pedir. Título igual ao commit principal (`feat(F4-0013): ...`), alvo `dev-v2` (de `dev-v2` para `main-v2` só em entrega revisada), corpo pelo template `.github/pull_request_template.md` com o ID. Merge de feature em `dev-v2` por squash; `dev-v2` -> `main-v2` por merge commit.
- **Toda fatia tem ID `S-NN`** (sequencial, único, nunca reaproveitado; `S-00` a `S-04` já existem). A pasta é `.scratch/S-NN-<nome>/` e o prefixo ordena as fatias por data de criação. Registre a fatia na tabela "Fatias" de `docs/tickets.md` (ID, nome, abertura, fechamento, tickets) ANTES de criar a pasta; a coluna Fatia do índice usa o nome completo (`S-02-plataforma-base`). O `spec.md` começa com `Fatia: S-NN`, `Aberta: AAAA-MM-DD`, `Fechada: AAAA-MM-DD` (ou `em andamento`) e `Tickets: F4-NNNN..F4-NNNN`. Nunca renomeie uma fatia publicada. Trabalho transversal (manutenção, CI, docs de processo) não é fatia: usa a fatia `governanca`, trilho permanente sem `S-NN` em `.scratch/governanca/` (texto de PR em `pr/F4-NNNN.md`, notas); o ID `F4-NNNN` vale como em qualquer ticket.
- **Arquivos do ticket:** `.scratch/<fatia>/issues/F4-NNNN-<resumo>.md` e `packets/F4-NNNN.md`, título `# F4-NNNN: ...`, `Blocked by` com IDs (ADR-016, F4-0113).
- **Fechar o ticket:** `**Status:** done` no arquivo do ticket e a linha em `docs/tickets.md` com branch, PR e status atualizados. Os 38 tickets anteriores são `F4-0001` a `F4-0038` (mapeados no índice); commits antigos não são reescritos.
- **Rastrear:** `git log --grep "F4-0013"`, busca de PR por `F4-0013`, `git branch -a | grep F4-0013`, e o índice.
- **Hooks locais (Husky, `.husky/`, instalados por `pnpm install` na raiz):** `pre-commit` bloqueia commit direto em `main-v2`, `main`, `staging`, `development` e `refactor-v2`, roda `dotnet format` nos `.cs` em staging recusa marcador de conflito e roda `gitleaks git --pre-commit --staged` (aviso, sem bloquear, se o binário não estiver instalado: `brew install gitleaks`; F4-0088); `commit-msg` exige `tipo(F4-NNNN): descrição` (merge/revert/fixup passam); `pre-push` bloqueia push direto nessas branches e roda `dotnet build`, os testes de arquitetura e unitários (sem Docker), `pnpm -r typecheck` e `pnpm -r --if-present test` (suíte completa com Docker: `PRE_PUSH_FULL=1 git push`). Não use `--no-verify`; o CI roda os mesmos gates. O front usa Biome (F4-0099): o `pre-commit` roda `biome check --staged --write` nos arquivos do front em staging (corrige formatação sozinho, bloqueia erro de lint, milissegundos); o `pre-push` roda `pnpm lint` na árvore inteira antes do typecheck e dos testes; o CI roda o mesmo (`lint-frontend`). Sem eslint nem prettier.

## Writing a route the secure way
1. Resolve the organization/unit from `OrganizationContext` (authenticated session), never from client input.
2. Declare `.RequirePermission(...)` on the route and authorize explicitly with a named permission before any work; deny by default.
3. Validate input at the Interface layer; pass typed commands to Application.
4. Scope every query by organization (and unit when applicable).
5. Audit state-changing actions (`IAuditLog`, see "Auditoria"); return no data the caller's permission does not cover.
6. Cover the route with a test for the allowed case and for the denied/cross-organization case.
(Step 2 is `IAuthorizationEngine.DecidirAsync`, see above and "A rota autenticada de ponta a ponta". Keep this list in sync with that section.)
