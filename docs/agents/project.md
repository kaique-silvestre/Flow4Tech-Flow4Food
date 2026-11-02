# Project commands

Run from repo root. Details in `CLAUDE.md`.

- dev: not defined yet (no runnable host until a later ticket)
- port: not defined yet
- lint: `dotnet format backend/Flow4Tech.sln` (check only: add `--verify-no-changes`)
- typecheck: `dotnet build backend/Flow4Tech.sln && pnpm -r typecheck`
- test: `dotnet test backend/Flow4Tech.sln`
