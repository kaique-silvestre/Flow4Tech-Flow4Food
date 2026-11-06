# Project commands

Run from repo root. Details in `CLAUDE.md`.

- dev: `docker compose up --build` (PostgreSQL + API)
- port: API 8080 (`/health/live`, `/health/ready`), PostgreSQL 5432
- lint: `dotnet format backend/Flow4Tech.sln` (check only: add `--verify-no-changes`)
- typecheck: `dotnet build backend/Flow4Tech.sln && pnpm -r typecheck`
- test: `dotnet test backend/Flow4Tech.sln` (Docker must be running: Testcontainers)
- contract (after API route/DTO change; commit both outputs): `dotnet build backend/src/Api/Flow4Tech.Api.csproj -p:GenerateContract=true && pnpm --filter @flow4tech/api-client generate`
- client tests: `pnpm -r --if-present test`
- isolation/RLS tests: `dotnet test backend/tests/Isolation/Flow4Tech.Isolation.Tests.csproj` (Docker required)
- deploy: see `docs/runbooks/deploy.md` (Railway + Vercel; `main` -> staging automatic, production manual). Migrator: `dotnet backend/src/Infrastructure/Migrator/bin/.../Flow4Tech.Migrator.dll` with `MIGRATION_DATABASE_URL`, `APP_DB_PASSWORD`, `MIGRATIONS_DIR`.
- workflow lint: `docker run --rm -v "$PWD:/repo" -w /repo rhysd/actionlint:latest`
- env config: `PORT`, `DATABASE_URL` or `ConnectionStrings__Default` (app credential); never commit values.
- front dev server: `pnpm --filter @flow4tech/app dev` (http://localhost:5173/salao; API paths are proxied to `API_URL`, default http://127.0.0.1:8080, so run `docker compose up --build` first for anything that calls the API)
- front build: `pnpm --filter @flow4tech/app build` (typecheck + production bundle + service worker in `frontend/app/dist`; the font is bundled, nothing is requested from a font service)
- vitest: `pnpm -r --if-present test` (design system components and tokens, app shell, generated client)
- e2e seed (local database only): `docker compose --profile e2e run --rm seed` after `docker compose up --build api`. Two organizations (owners `dono@bar-a.test` and `dono@bar-b.test`), units Centro and Praia in the first and Centro in the second, operational users `garcom` (profile Garçom), `caixa` (profile Caixa) and `novato` (temporary credential, forces a password change; the browser tests ask the owner for a fresh one through `POST /contas/{id}/redefinir-acesso`), `gerente` (both units) and `semunidade` (no unit) in the first, six tables, four products. The password is `E2E_SEED_PASSWORD` (`e2e-local-throwaway-password` in `docker-compose.yml`, a local throwaway). Idempotent; it writes the ids of the accounts to `frontend/app/e2e-results/seed.json` (create the folder first; the browser tests read it). Without compose: set `E2E_SEED_OWNER_URL`, `E2E_SEED_APP_URL`, `E2E_SEED_PASSWORD` and run `dotnet run --project backend/tools/E2eSeed`.
- e2e (Playwright, 390x844 and 360x740, writes screenshots to `frontend/app/e2e-results/screenshots`): `pnpm --filter @flow4tech/app exec playwright install chromium` once, then `E2E_API=1 pnpm --filter @flow4tech/app e2e` (it builds and serves the production bundle with `vite preview` on 4173). Without the API running, leave `E2E_API` unset: the tests that need it are skipped. In a cloud session where the Playwright browser download is not available, set `PW_CHROMIUM` to an installed Chromium.
