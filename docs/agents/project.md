# Project commands

Run from repo root. Details in `CLAUDE.md`.

- dev: `docker compose up --build` (PostgreSQL + API)
- port: API 8080 (`/health/live`, `/health/ready`), PostgreSQL 5432
- lint: `dotnet format backend/Flow4Tech.sln` (check only: add `--verify-no-changes`)
- typecheck: `dotnet build backend/Flow4Tech.sln && pnpm -r typecheck`
- test: `dotnet test backend/Flow4Tech.sln` (Docker must be running: Testcontainers)
- contract (after API route/DTO change; commit both outputs): `dotnet build backend/src/Api/Flow4Tech.Api.csproj -p:GenerateContract=true && pnpm --filter @flow4tech/api-client generate`
- client tests: `pnpm -r --if-present test`
