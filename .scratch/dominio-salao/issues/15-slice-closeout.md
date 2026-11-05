# 15: Slice closeout: two tenants end to end, CI, docs

**What to build:** The whole slice is proven with two organizations through real authenticated routes and through the browser, the CI pipeline runs every gate on the branch for the first time, and the project documentation describes the new modules.
**Blocked by:** 13, 14
**Touches:** backend/tests/Flow4Tech.Tests/DoisTenantsEndpointsTests.cs (or a sibling), frontend e2e specs, CLAUDE.md, docs/agents/project.md, .scratch/dominio-salao/state.md
**Nature:** objective
**tdd-scope:** required
**Seams:** HTTP with real PostgreSQL; UI in the browser; Architecture gates
**Status:** ready-for-agent

- [ ] A two-organization HTTP test runs the full flow (real login, open shift, open comanda, launch, pay in cash, close comanda, close shift) in each organization and the sweep of every table with `organizacao_id` still returns zero crossing rows and zero rows without context.
- [ ] Playwright runs the full flow at 390x844 and 360x740 with screenshots for every state in the DESIGN.md Salão section; the owner reviews the screenshots and the section's `approved:` line is filled.
- [ ] `dotnet build` without warnings, `dotnet format --verify-no-changes` clean, `pnpm -r typecheck` green, `dotnet test backend/Flow4Tech.sln` and `pnpm -r --if-present test` pass with real PostgreSQL and the real API; the GitHub Actions pipeline runs every gate on the branch (first real run) and the result is recorded in `state.md`.
- [ ] `CLAUDE.md` gains sections for the four modules (tables, permissions, settings, audit actions, events, routes, the receive-and-close unit of work); ADR-014 and ADR-015 are approved.
- [ ] The two stories' measurable targets are recorded: first item in under 10 seconds and at most 2 taps for the next, from the scripted flow.
