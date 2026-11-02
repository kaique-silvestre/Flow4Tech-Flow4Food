# 05: OpenAPI contract and generated TypeScript client

**What to build:** The API publishes an OpenAPI contract generated from code; a typed TypeScript client is generated from it into the front workspaces; a contract break fails the CI.
**Blocked by:** 01, 04
**Touches:** `backend/src/Api/**`, `frontend/packages/api-client/**`, `.github/workflows/**`
**Nature:** objective
**tdd-scope:** required
**Seams:** Contract (OpenAPI)
**Status:** done-local (pending-owner steps below)

- [ ] OpenAPI document is generated from the API and checked in or published as a build artifact
- [ ] TypeScript client is generated from the document and imported by `app` and `control` workspaces
- [ ] CI regenerates the client and fails if the committed output differs
- [ ] CI fails on a breaking change to the contract (proven with a deliberate change)
- [ ] One trivial endpoint (e.g. version/health) is consumed through the generated client in a test

## Pending-owner
Needs a real GitHub Actions run (cannot be executed locally):
- [ ] Push the branch/open a PR and confirm job `openapi-contract` is green and `gates` includes it; the oasdiff step is skipped on the very first revision (no base contract), so open a follow-up PR with a deliberate breaking change (e.g. rename `version` in `VersionInfo`) and confirm it fails the PR, then close it.
- [ ] Note: `openapi-contract` is covered by the existing required check `gates`; no branch-protection change needed.
- [ ] Confirm `docker run tufin/oasdiff@sha256:143f8444...` pulls on `ubuntu-latest` (verified locally only).
