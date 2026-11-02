# 04: CI pipeline with mandatory gates

**What to build:** Every pull request runs one pipeline with the mandatory gates; merge is blocked unless it is green and only the main branch deploys.
**Blocked by:** 02, 03
**Touches:** `.github/workflows/**` (or the chosen CI), `CLAUDE.md`
**Nature:** objective
**tdd-scope:** not-applicable
**Seams:** Pipeline run (CI)
**Status:** done-local (pending-owner steps below)

- [ ] Pipeline runs build, type checks, lint, unit tests, integration tests with real PostgreSQL, architecture tests, secret scanning and vulnerable-dependency check
- [ ] A deliberately failing change in each gate is shown to turn the pipeline red (evidence in the PR)
- [ ] Branch protection requires the green pipeline to merge
- [ ] Pipeline duration is recorded as a budget in `CLAUDE.md`
- [ ] No secret is stored in the repository; secret scanning covers history

## Pending-owner
Needs GitHub access; not doable locally.
1. First real pipeline run: push the branch, open a PR to `main`, confirm all jobs in `ci` pass (watch for: pnpm version 11 resolving in `pnpm/action-setup`, gitleaks download URL/version 8.28.0, `environment: production` is auto-created on first deploy).
2. PR evidence of red gates: on throwaway branches (or commits in the PR, then reverted) apply one failing change per gate and link the red runs in the PR. Recipes proven locally: unused local variable (build); mis-indented line (format); `export const x: number = "a";` in `frontend/app/src` (typecheck); `Assert.Equal(8, ...)` in `KernelSmokeTests` (unit); wrong status in `HealthEndpointsTests` (integration); `ProjectReference` Permissoes.Domain -> Organizacao.Domain (architecture); commit a fake `AKIA...` key and remove it in a later commit (secret scan, history); `pnpm add lodash@4.17.20` (pnpm audit); `System.Text.Json` 8.0.0 in Kernel (NuGet).
3. Branch protection (Settings > Branches > Add rule for `main`, or `gh`):
   - UI: Require a pull request before merging; Require status checks to pass; Require branches up to date; add check `gates`; Do not allow bypassing; also restrict deploy `environment: production` to `main` (Settings > Environments).
   - CLI: `gh api -X PUT repos/<owner>/<repo>/branches/main/protection --input - <<'JSON'` with `{"required_status_checks":{"strict":true,"contexts":["gates"]},"enforce_admins":true,"required_pull_request_reviews":{"required_approving_review_count":0},"restrictions":null}` then `JSON`. (If using rulesets, require status check `gates`.) Private repos need GitHub Pro/Team for protection.
4. Measured duration: read the total and per-job times from the first green run (Actions > run summary), replace the TARGET in `CLAUDE.md` "CI gates" with the measured values.
5. Optional hardening: pin actions to commit SHAs; enable Dependabot / GitHub secret scanning push protection.

