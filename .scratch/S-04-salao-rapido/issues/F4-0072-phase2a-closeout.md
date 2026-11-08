# F4-0072: Phase 2A closeout: two tenants, measured targets, shadow-pilot script

**What to build:** Prove the whole of 2A and hand the owner a script to run the shadow pilot with real waiters.
**Blocked by:** F4-0044, F4-0045, F4-0047, F4-0049, F4-0051, F4-0053, F4-0055, F4-0057, F4-0059, F4-0062, F4-0064, F4-0066, F4-0068, F4-0070, F4-0071
**Touches:** backend/tests/Flow4Tech.Tests/DoisTenantsEndpointsTests.cs, frontend/app/e2e/**, CLAUDE.md, docs/**, .scratch/S-04-salao-rapido/**
**Nature:** objective
**tdd-scope:** required
**Seams:** Two tenants over HTTP (`DoisTenantsEndpointsTests` extended); Browser (Playwright against the real API in `docker compose`, 390x844 and 360x740) plus vitest/testing-library for logic, state and components.
**Status:** ready-for-agent

- [ ] A scripted night in each of two organizations over HTTP: model profiles, import, add-ons, repeat round, discount within and above the limit, transfer, merge, every split form, mixed payment, supply, withdrawal, close with an approved difference; table sweep stays at zero across organizations.
- [ ] Full suites green: `dotnet test backend/Flow4Tech.sln`, `pnpm -r typecheck`, `pnpm -r --if-present test`, Playwright at both viewports against the real API; `dotnet format --verify-no-changes` clean; the GitHub Actions run of every gate green on the PR (first real run) or listed as the remaining owner item.
- [ ] Targets measured on the real API and written to `metas.txt`: first item under 10 s; common item 2 taps; equal split 4 taps; mixed payment 8 taps.
- [ ] CLAUDE.md gains the new modules/permissions/options; ADR-017/018 accepted; `backend/*.catalog.txt` current.
- [ ] `.scratch/S-04-salao-rapido/piloto-sombra.md`: the night script, who does what, the friction sheet, how to reset the shadow data, and the rollback plan (shadow data never feeds anything official).
