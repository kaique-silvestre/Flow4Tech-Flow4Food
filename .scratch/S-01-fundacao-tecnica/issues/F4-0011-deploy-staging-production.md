# F4-0011: Deploy to staging and production

**What to build:** Main branch deploys automatically to `staging`; `production` is provisioned. Railway hosts API and PostgreSQL per environment, Vercel hosts the front with branch previews. Migrations run on deploy with a credential separate from the application's.
**Blocked by:** F4-0004, F4-0006
**Touches:** `railway.toml` (or equivalent), `.github/workflows/**`, `frontend/**/vercel.*`, `docs/runbooks/deploy.md`
**Nature:** objective
**tdd-scope:** not-applicable
**Seams:** Pipeline run (CI), Integration (post-deploy smoke)
**Status:** done-local (pending-owner steps P1-P13 in this file)

- [ ] (done-local: `ci.yml` `deploy-staging` -> `deploy.yml`, actionlint clean; pending-owner: P1-P5, P8) Merge to main on a green pipeline deploys to `staging` without manual steps
- [ ] (done-local: `deploy-production.yml`, per-environment tokens/variables by design; pending-owner: P1, P2, P4) `production` environment exists with its own database and variables; no production data is used in `staging`
- [ ] (done-local: no secret in repo, names documented; pending-owner: P3, P4, P6) Environment variables are set per environment; no secret is in the repository
- [ ] (done-local: migrator + pre-deploy + restricted-credential check proven on real PostgreSQL and docker compose; pending-owner: P3, P8, P9 for staging) Migrations run during deploy using the migration credential; the application runs with the restricted application credential (verified in `staging`)
- [ ] (done-local: `railway.toml` healthcheckPath; pending-owner: P8 confirms Railway honors it) Healthcheck on Railway uses the readiness endpoint
- [ ] (done-local: `vercel.json` x2, build command run locally; pending-owner: P7, P8) Front deploys on Vercel with a preview per branch
- [x] (done-local: `docs/runbooks/deploy.md`) Runbook documents the deploy flow

## Pending-owner
Everything below needs Railway, Vercel or GitHub access and was NOT done or verified locally. Details in `docs/runbooks/deploy.md` (First-time setup).

- P1. Railway: create the project, add PostgreSQL and the API service from this repo (root `railway.toml` is read), create environments `staging` and `production`, each with its OWN PostgreSQL service; region US East. Completes: staging deploy, production exists with own database, no production data in staging.
- P2. Railway: generate a public domain per environment; create one environment-scoped project token per environment. Completes: staging deploy, production environment.
- P3. Railway variables per environment on the API service: `MIGRATION_DATABASE_URL` (owner URL of that environment's PostgreSQL), `APP_DB_PASSWORD` (fresh random secret per environment), `DATABASE_URL` (`postgres://flow4tech_app:<APP_DB_PASSWORD>@<host>:<port>/<db>`). Completes: variables per environment, migration credential vs restricted app credential.
- P4. GitHub environments: `staging` (no reviewers) and `production` (required reviewers, restricted to `main`); in each: secret `RAILWAY_TOKEN`, variables `RAILWAY_SERVICE`, `API_BASE_URL`. Completes: staging deploy without manual steps, production approval, variables per environment.
- P5. GitHub branch protection on `main`: require only the `gates` check. Completes: staging deploys only from green main.
- P6. Confirm no secret value was committed (gitleaks job stays green on the first run). Completes: no secret in repo.
- P7. Vercel: import the repo twice (root directories `frontend/app`, `frontend/control`, source outside root enabled), production branch `main`, per-branch previews on, set the API base URL variable per environment. Completes: front on Vercel with preview per branch.
- P8. First real deploy by pushing to `main`: confirm that (a) the Railway pre-deploy command runs `Flow4Tech.Migrator.dll` and logs `applied ...` and `application credential verified`, (b) Railway uses `/health/ready` as healthcheck, (c) `railway up --ci --service --environment` with a project token works as written in `deploy.yml` (CLI flags, exit behavior; pin the CLI version once known), (d) the smoke test passes, (g) `railway up --ci` semantics (assumed: waits for the build only, not the rollout) and that `railway variables --set GIT_SHA=... --skip-deploys` works as written; the smoke test ties readiness to the new SHA through the `commit` field of `/health/ready`, so this must be seen passing once, (f) UNVERIFIED railway.toml assumptions: the `preDeployCommand` form (array vs single string, shell or not; I could not confirm the documented form offline, change it if Railway says otherwise) and that the pre-deploy container sees the private network and the service variables, (e) the Vercel preview URL of a branch loads. Completes: staging deploy, healthcheck, migrations on deploy, Vercel previews.
- P9. Verify the restricted credential in staging by hand with the SQL in the runbook (`flow4tech_app` not superuser, no BYPASSRLS, DDL refused, zero rows without context). Completes: "application runs with the restricted credential (verified in staging)".
- P10. Production: dispatch `deploy-production` with the SHA that ran in staging, approve, repeat the P9 check. Completes: production provisioned.
- P11. Decide, if the threat model asks, whether the owner secret may sit in the API service variables (Railway pre-deploy uses service variables) or needs a separate migrate service (ADR-009 consequences).
- P12. Pin GitHub Actions to commit SHAs (or rely on Dependabot PRs from `.github/dependabot.yml`) and pin the Railway CLI in `deploy.yml` to an exact version; enable Dependabot alerts/updates in the repository. Completes: supply-chain hygiene of the deploy path (all criteria depend on it).
- P13. Do NOT set `SKIP_APP_CHECK` on the API service in `staging` or `production` (the restricted-credential check is required by default; a missing `DATABASE_URL` fails the pre-deploy). Completes: restricted credential verified on every deploy.

Isolation criterion note: "isolation check with the application credential in staging" is covered by two things, the automatic migrator check on every deploy (restricted role: no superuser, no BYPASSRLS, no ownership or owner membership, no CREATE, DDL refused) plus the manual probe in P9 (zero rows without an organization context). There is no automated cross-organization probe against staging data.
