# Deploy runbook

Hosting decision: [ADR-009](../adr/009-hospedagem-railway-vercel.md) (amended by F4-0080). Everything below uses variable NAMES only; values live in Railway, never in the repo.

## Environments

The new system lives in its own Railway project, `flow4tech-v2` (the old system is the separate project `flow4food`; never touch it). Two environments, each with its own PostgreSQL and API service:

| Environment | Git branch | When it deploys | Public URL |
|---|---|---|---|
| `staging` | `dev-v2` | every push to `dev-v2`, after the GitHub checks (`ci` job `gates`) are green | `https://flow4tech-api-staging.up.railway.app` |
| `production` | `main-v2` | every merge to `main-v2` (PR only, from `dev-v2`), after the checks are green | not created yet |
| front | per ADR-009 (Vercel) | Vercel Git integration, preview per branch | see `.scratch/governanca/hospedagem-do-front-e-ambientes.md` (open discussion) |

`staging` and `production` never share a database, token or secret. There is no manual approval step in Railway: the protection of production is the PR into `main-v2` plus the required `gates` check (configure branch protection on GitHub, see First-time setup).

## How a deploy happens

1. A commit lands on `dev-v2` (direct push or squash-merged PR) or `main-v2` (PR). The `ci` workflow runs on every PR and on every push to `dev-v2`, `main-v2` and `main` (ADR-016).
2. The service has "Wait for CI" on (`checkSuites: true` in `.railway/railway.ts`): Railway deploys only if the GitHub checks of that commit pass. A red `gates` means no deploy.
3. Railway builds the root `Dockerfile` (one image, two entrypoints: the API and `Flow4Tech.Migrator.dll`).
4. Pre-deploy command `dotnet Flow4Tech.Migrator.dll` (owner credential, private network):
   - applies `backend/migrations` (checksummed, advisory-locked; an edited applied file fails the deploy);
   - sets LOGIN and password of `flow4tech_app` from `APP_DB_PASSWORD` (idempotent);
   - connects with the application credential (`DATABASE_URL`) and fails the deploy if it is a superuser, has BYPASSRLS, CREATEROLE/CREATEDB, owns tables, is a member of the owner or database-owner role or of pg_write_all_data/pg_read_all_data/pg_execute_server_program/pg_write_server_files/pg_read_server_files, has CREATE on schema public, or can create objects. This is the automatic check that the app runs restricted, on every deploy.
5. Railway starts the container and waits for `GET /health/ready` (200 only if the database answers with the app credential, 60 s). If it fails, the previous deployment keeps serving.
6. Check it: `curl https://<domain>/health/ready` returns `{"status":"ready","commit":"<sha>"}`; `commit` must equal the SHA you expect (`RAILWAY_GIT_COMMIT_SHA`).

Not used for the new system anymore: `railway up`, a Railway token in GitHub, the `deploy.yml` and `deploy-production.yml` workflows (removed in F4-0080).

## Railway configuration is code: `.railway/railway.ts`

The service settings (GitHub source and branch per environment, wait for CI, Dockerfile build, pre-deploy command, healthcheck) live in `.railway/railway.ts`, not in the dashboard and not in `railway.toml`.

- `railway.toml` is dead for us: Railway ignores it for services that never used Config as Code (the opt-in closed on 2026-08-28). That is why staging once ran with no pre-deploy and no healthcheck and a 503 counted as SUCCESS. Do not bring it back.
- The file is NOT read at deploy time. Changing it changes nothing until someone applies it, once per environment:
  ```
  railway environment staging        # or production
  railway config plan                # shows the diff, changes nothing; read it
  railway config apply               # applies after showing the plan (--yes skips the prompt)
  ```
  `plan` must say "0 to destroy" unless you meant to delete something. After the apply, `plan` again must say "already up to date".
- What the file does NOT manage: variables and secrets (`variables: { managed: false }`: they live only in Railway), the PostgreSQL service and the public domain (created with the CLI, see below). The branch is chosen by environment inside the file (`ctx.isEnvironment("production")`).
- The SDK is the dev dependency `railway` at the workspace root (`pnpm install` brings it); the CLI needs it to evaluate the file.
- See the applied configuration of a deployment: `railway deployment list -s flow4tech-api -e staging --limit 1 --json` and read `meta.serviceManifest.deploy` (`preDeployCommand`, `healthcheckPath`).
- Pre-deploy logs are not in `railway service logs`; look at the deployment steps in the Railway dashboard (Initialization, Build, Pre-deploy, Deploy, Post-deploy).

## Credentials model (ADR-002)

| Credential | Where | Used by | Powers |
|---|---|---|---|
| owner / migration | Railway: `MIGRATION_DATABASE_URL` (a reference to the environment's PostgreSQL URL) | pre-deploy migrator only (the web process does not read it) | DDL, creates the app role, owns tables |
| application `flow4tech_app` | Railway: `DATABASE_URL` (`postgres://flow4tech_app:<APP_DB_PASSWORD>@<host>:<port>/<db>`) | the running API | DML under RLS; no DDL, no BYPASSRLS, not owner |

Caveat: Railway pre-deploy runs with the service's variables, so `MIGRATION_DATABASE_URL` is visible in the API service's environment although the web process never reads it. Use a hex password (`openssl rand -hex 24`) for `APP_DB_PASSWORD` so the URL needs no percent-encoding; `DATABASE_URL` values with special characters must be percent-encoded; an unencoded `#`, `?` or `/` in the password, unknown query parameters (only `sslmode` is accepted) and IPv6 hosts in `[...]` form are handled/refused explicitly, never silently truncated.

## Variables per environment (names only)

Railway, API service `flow4tech-api` (set separately in `staging` and `production`, with `railway variable set -s flow4tech-api -e <env> --skip-deploys ...`; for a secret use `--stdin` so it never appears in a command line or log):
- `DATABASE_URL`: application credential URL. Built from references: `postgres://flow4tech_app:${{APP_DB_PASSWORD}}@${{Postgres.PGHOST}}:${{Postgres.PGPORT}}/${{Postgres.PGDATABASE}}`. `ConnectionStrings__Default` also works and wins when both are set.
- `MIGRATION_DATABASE_URL`: owner credential URL, `${{Postgres.DATABASE_URL}}` (the private-network URL of the environment's PostgreSQL). `ConnectionStrings__Migration` also works.
- The restricted-credential check is REQUIRED by default: a missing `DATABASE_URL` fails the pre-deploy (exit 1). `SKIP_APP_CHECK=1` is the explicit opt-out for a migrate-only run; never set it in staging or production.
- `APP_DB_PASSWORD`: password of `flow4tech_app`, secret, different per environment (`openssl rand -hex 24`).
- `Flow4Tech__Tokens__AppKey`: signing key of the access tokens (ticket 04, ADR-010), secret, at least 32 bytes, different per environment (`openssl rand -base64 48`). REQUIRED: the API does not start without it outside Development/Testing. To rotate: move the current value to `Flow4Tech__Tokens__AppKeyPrevious`, set a new `AppKey`, deploy; after the access token lifetime (5 minutes by default, plus 30 s of skew) remove `AppKeyPrevious`. `Flow4Tech__Tokens__ControlKey` is reserved for the Control Tower realm (phase 1B).
- `Flow4Tech__Rede__ProxyConfiavel=true`: the API is reachable only through Railway's proxy (last `X-Forwarded-For` entry).
- Optional tuning: `Flow4Tech__Sessoes__InatividadeHoras` (12), `Flow4Tech__Sessoes__AbsolutaDias` (14), `Flow4Tech__Sessoes__AllowedOrigins__0` (the front origin when it is not the API's own).
- `GIT_SHA` is not set: `/health/ready` falls back to `RAILWAY_GIT_COMMIT_SHA`, which Railway injects.
- `PORT`: injected by Railway; the app binds to it (8080 when absent). Do not set it.
- Optional: `MIGRATIONS_DIR` (default `/app/migrations`, already in the image).

Vercel (per project `app` and `control`, per environment Preview/Production): the API base URL of the matching backend. Note the refresh cookie is `SameSite=Strict`; the front and the API must be the same site (own domain) or the front must proxy the API: see `.scratch/governanca/hospedagem-do-front-e-ambientes.md`.

## Verify the restricted credential in staging

Automatic: the pre-deploy step fails the deploy if the check fails (it prints `application credential verified`). Manual, run the migrator inside the container (idempotent; it prints each step):

```
railway ssh -s flow4tech-api -e staging -- dotnet Flow4Tech.Migrator.dll
```

It needs a registered SSH key (`railway ssh keys add -k <public key file>`; `railway ssh keys github` may lack permission) and the host key accepted once in a real terminal (`ssh -i <key> <user>@ssh.railway.com`, answer `yes`; the `User` comes from `railway ssh config --dry-run`). The `libgssapi_krb5.so.2` line it prints is Npgsql noise, not an error.

Or, from any machine that can reach the database (`railway connect`):

```sql
-- as flow4tech_app (the DATABASE_URL credential)
SELECT current_user, rolsuper, rolbypassrls FROM pg_roles WHERE rolname = current_user; -- flow4tech_app | f | f
CREATE TABLE x (i int);                      -- ERROR: permission denied for schema public
SELECT count(*) FROM organizacoes;           -- 0 without an organization context (RLS)
```

Requires PostgreSQL 15 or newer: the "app cannot create objects" guarantee relies on PUBLIC no longer having CREATE on schema public (the migration also revokes it explicitly, and the check verifies it).

## Rollback

1. App only: Railway dashboard, service, Deployments, redeploy the previous successful deployment (or `railway service redeploy -s flow4tech-api -e <env> --commit <SHA>`). Migrations are expand-migrate-contract, so the previous version works with the schema left by the new one.
2. A bad migration: never edit an applied file. Add a new forward migration that repairs it and deploy. Data-destructive "contract" steps ship only after the version that stops using the column is in production.
3. Failed deploy: Railway keeps the previous deployment serving when the pre-deploy command or the healthcheck fails; fix forward or revert the commit on the branch.

## First-time setup of an environment (checklist, what was done for `staging`)

1. `railway init --name flow4tech-v2` (once), `railway environment new staging` (the project starts with `production`).
2. In the environment: `railway add --database postgres`, then `railway add --service flow4tech-api` ("Empty Service"; the GitHub source comes from `.railway/railway.ts`).
3. Variables (list above). Secrets are generated with `openssl` and piped with `--stdin`; check names and lengths only, never print values.
4. `railway domain -s flow4tech-api -e <env>` for the public URL.
5. Connect the GitHub source and apply the file: `railway service source connect --repo kaique-silvestre/Flow4Tech-Flow4Food --branch <dev-v2|main-v2> --service flow4tech-api`, then `railway config plan` and `railway config apply` (see above).
6. Push (or `railway service redeploy --from-source`), watch the Pre-deploy step in the dashboard, then `curl /health/ready`.
7. GitHub (settings, owner decision): branch protection on `main-v2` requiring PR and the `gates` check; `dev-v2` allows direct push (the CI still runs on it).
8. Vercel: import the repo twice (root directories `frontend/app` and `frontend/control`, "include source files outside of the root directory" on); keep per-branch preview on (pending the hosting discussion).

## Local equivalent

`docker compose up --build` runs the same model: `migrate` (owner `flow4tech`) then `api` (restricted `flow4tech_app`). `curl localhost:8080/health/ready`.
