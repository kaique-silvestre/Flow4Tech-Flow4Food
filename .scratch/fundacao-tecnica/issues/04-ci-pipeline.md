# 04: CI pipeline with mandatory gates

**What to build:** Every pull request runs one pipeline with the mandatory gates; merge is blocked unless it is green and only the main branch deploys.
**Blocked by:** 02, 03
**Touches:** `.github/workflows/**` (or the chosen CI), `CLAUDE.md`
**Nature:** objective
**tdd-scope:** not-applicable
**Seams:** Pipeline run (CI)
**Status:** ready-for-agent

- [ ] Pipeline runs build, type checks, lint, unit tests, integration tests with real PostgreSQL, architecture tests, secret scanning and vulnerable-dependency check
- [ ] A deliberately failing change in each gate is shown to turn the pipeline red (evidence in the PR)
- [ ] Branch protection requires the green pipeline to merge
- [ ] Pipeline duration is recorded as a budget in `CLAUDE.md`
- [ ] No secret is stored in the repository; secret scanning covers history
