# Deploy runbook

Hosting decision: [ADR-009](../adr/009-hospedagem-railway-vercel.md). Everything below uses variable NAMES only; values live in Railway and GitHub, never in the repo.

## Environments

| Environment | Branch / trigger | API + PostgreSQL | Front | Approval |
|---|---|---|---|---|
| preview (front only) | any branch / PR | none | Vercel preview URL per branch | none |
| `staging` | push to `main`, after the `gates` job is green | Railway environment `staging` (own database) | Vercel (Git integration, preview/staging domain) | none |
| `production` | manual: workflow `deploy-production` | Railway environment `production` (own database) | Vercel production branch `main` | required reviewers on the GitHub environment `production` |

`staging` and `production` never share a database, token or secret; no production data is copied to `staging`.

## Flow

1. Pull request: all gates in `.github/workflows/ci.yml` run. `gates` is the only required check. The `build` job also builds the Docker image, so a broken image fails the PR.
2. Merge to `main`: `ci` runs again; `deploy-staging` (reusable `deploy.yml`, GitHub environment `staging`) starts only if `gates` succeeded.
3. `deploy.yml`: `railway up --ci` uploads the commit and Railway builds the root `Dockerfile` (`railway.toml`).
4. Railway pre-deploy command `dotnet Flow4Tech.Migrator.dll` (owner credential, private network):
   - applies `backend/migrations` (checksummed, advisory-locked; an edited applied file fails the deploy);
   - sets LOGIN and password of `flow4tech_app` from `APP_DB_PASSWORD` (idempotent);
   - connects with the application credential (`DATABASE_URL`) and fails the deploy if it is a superuser, has BYPASSRLS, CREATEROLE/CREATEDB, owns tables, is a member of the owner or database-owner role or of pg_write_all_data/pg_read_all_data/pg_execute_server_program/pg_write_server_files/pg_read_server_files, has CREATE on schema public, or can create objects. This is the automatic check that the app runs restricted, on every deploy.
5. Railway starts the new container and waits for `GET /health/ready` (200 only if the database answers with the app credential, 60 s). If it fails the old deployment keeps serving.
6. The workflow smoke test calls `${API_BASE_URL}/health/ready` until it answers 200 AND reports `commit` equal to the deployed SHA (60 attempts, 10 s apart), so an old deployment still serving cannot pass. `railway up --ci` is not trusted to wait for the rollout (unverified, see ticket P8).
7. Production: after a version ran in staging, run workflow `deploy-production` with that commit SHA. A `verify` job checks the SHA is on `main` and has a successful `ci` run; the `production` environment then waits for a reviewer, and the same `deploy.yml` runs against Railway `production` using the resolved SHA (not the raw input).
8. Front: Vercel builds on every push through its Git integration (`frontend/app/vercel.json`, `frontend/control/vercel.json`). Until the apps have a real build, the build command typechecks and emits a placeholder page; replace it with the real build when the UI exists.

## Credentials model (ADR-002)

| Credential | Where | Used by | Powers |
|---|---|---|---|
| owner / migration | Railway: `MIGRATION_DATABASE_URL` (the Railway PostgreSQL connection URL) | pre-deploy migrator only (the web process does not read it) | DDL, creates the app role, owns tables |
| application `flow4tech_app` | Railway: `DATABASE_URL` (`postgres://flow4tech_app:<APP_DB_PASSWORD>@<host>:<port>/<db>`) | the running API | DML under RLS; no DDL, no BYPASSRLS, not owner |

Caveat: Railway pre-deploy runs with the service's variables, so `MIGRATION_DATABASE_URL` is visible in the API service's environment although the web process never reads it. Use a hex password (`openssl rand -hex 24`) for `APP_DB_PASSWORD` so the URL needs no percent-encoding; ``DATABASE_URL` values with special characters must be percent-encoded; an unencoded `#`, `?` or `/` in the password, unknown query parameters (only `sslmode` is accepted) and IPv6 hosts in `[...]` form are handled/refused explicitly, never silently truncated.

## Variables per environment (names only)

Railway, API service (set separately in `staging` and `production`):
- `DATABASE_URL`: application credential URL (above). `ConnectionStrings__Default` also works and wins when both are set.
- `MIGRATION_DATABASE_URL`: owner credential URL (`ConnectionStrings__Migration` also works).
- The restricted-credential check is REQUIRED by default: a missing `DATABASE_URL` fails the pre-deploy (exit 1). `SKIP_APP_CHECK=1` is the explicit opt-out for a migrate-only run; never set it in staging or production.
- `GIT_SHA`: written by `deploy.yml` before each deploy (`RAILWAY_GIT_COMMIT_SHA` is the fallback); `/health/ready` returns it as `commit` and the smoke test requires it to equal the deployed SHA.
- `APP_DB_PASSWORD`: password of `flow4tech_app`, secret, different per environment.
- `Flow4Tech__Tokens__AppKey`: signing key of the access tokens (ticket 04, ADR-010), secret, at least 32 bytes, different per environment (`openssl rand -base64 48`). REQUIRED: the API does not start without it outside Development/Testing. To rotate: move the current value to `Flow4Tech__Tokens__AppKeyPrevious`, set a new `AppKey`, deploy; after the access token lifetime (5 minutes by default, plus 30 s of skew) remove `AppKeyPrevious`. `Flow4Tech__Tokens__ControlKey` is reserved for the Control Tower realm (phase 1B). Optional tuning: `Flow4Tech__Sessoes__InatividadeHoras` (12), `Flow4Tech__Sessoes__AbsolutaDias` (14), `Flow4Tech__Sessoes__AllowedOrigins__0` (the front origin when it is not the API's own).
- `PORT`: injected by Railway; the app binds to it (8080 when absent). Do not set it.
- Optional: `MIGRATIONS_DIR` (default `/app/migrations`, already in the image).

GitHub environments `staging` and `production`:
- secret `RAILWAY_TOKEN`: Railway project token of that Railway environment.
- variable `RAILWAY_SERVICE`: API service name or id.
- variable `API_BASE_URL`: public API URL (`https://...`, no trailing slash).

Vercel (per project `app` and `control`, per environment Preview/Production): the API base URL of the matching backend (variable name defined with the first UI that reads it).

## Verify the restricted credential in staging

Automatic: the pre-deploy step fails the deploy if the check fails (look for `application credential verified` in the pre-deploy log). Manual, from any machine that can reach the database (Railway TCP proxy URL or `railway connect`):

```sql
-- as flow4tech_app (the DATABASE_URL credential)
SELECT current_user, rolsuper, rolbypassrls FROM pg_roles WHERE rolname = current_user; -- flow4tech_app | f | f
CREATE TABLE x (i int);                      -- ERROR: permission denied for schema public
SELECT count(*) FROM organizacoes;           -- 0 without an organization context (RLS)
```

Requires PostgreSQL 15 or newer: the "app cannot create objects" guarantee relies on PUBLIC no longer having CREATE on schema public (the migration also revokes it explicitly, and the check verifies it).

## Rollback

1. App only: Railway dashboard, service, Deployments, redeploy the previous successful deployment (or re-run `deploy-production` with the previous SHA). Migrations are expand-migrate-contract, so the previous version works with the schema left by the new one.
2. A bad migration: never edit an applied file. Add a new forward migration that repairs it and deploy. Data-destructive "contract" steps ship only after the version that stops using the column is in production.
3. Failed deploy: Railway keeps the previous deployment serving when the pre-deploy command or the healthcheck fails; fix forward or revert the commit on `main`.

## First-time setup (once, by the owner)

1. Railway: create a project; add PostgreSQL; create environments `staging` and `production` (each with its own PostgreSQL service); region US East (Virginia).
2. In each environment add the API service from this GitHub repo (root `railway.toml` is picked up), set the variables above, generate a public domain.
3. Create one Railway project token per environment.
4. GitHub: Settings, Environments: create `staging` (no reviewers) and `production` (required reviewers); add the secret and variables above in each. Restrict `production` to the `main` branch.
5. GitHub branch protection on `main`: require the `gates` check only.
6. Vercel: import the repo twice (root directories `frontend/app` and `frontend/control`, "include source files outside of the root directory" on); set production branch `main`; keep per-branch preview on.
7. First deploy: push to `main`; watch pre-deploy logs (`applied 0001...`, `application credential verified`), then the smoke test; then run the manual SQL check above.
8. Production: dispatch `deploy-production` with the SHA that ran in staging and approve.

## Local equivalent

`docker compose up --build` runs the same model: `migrate` (owner `flow4tech`) then `api` (restricted `flow4tech_app`). `curl localhost:8080/health/ready`.
