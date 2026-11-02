# 009. Hosting: Railway (API + PostgreSQL) and Vercel (front), staging and production

- Status: accepted (decision taken in `.scratch/fundacao-tecnica/spec.md`, section 5.2, and 04-arquitetura A10; this record is written from that spec decision, ticket 11)
- Date: 2026-11-02

## Context
The foundation needs two environments, `staging` and `production`, each with its own database, deploys from green `main`, migrations on deploy with a credential separate from the application's, and a preview of the front per branch. The architecture is a modular monolith in one standard Linux container configured by environment variables (CLAUDE.md, ADR-002).

## Decision
1. **API and PostgreSQL on Railway**, one Railway environment per `staging` and `production`, each with its own PostgreSQL service and variables. Region US East (Virginia): Railway has no Brazil region (04-arquitetura A10). Built from the root `Dockerfile` (`railway.toml`); healthcheck `/health/ready`.
2. **Front (`app`, `control`) on Vercel**, preview per branch (Vercel Git integration default), variables per environment.
3. **Deploy pipeline**: GitHub Actions. `ci.yml` deploys `staging` automatically from `main` after the `gates` job; `production` is a separate manual workflow behind a GitHub environment with required reviewers. Both use the Railway CLI with an environment-scoped project token and finish with a readiness smoke test.
4. **Two database credentials** (ADR-002): the owner (migrations) and `flow4tech_app` (running app). Migrations run in Railway's pre-deploy command (`dotnet Flow4Tech.Migrator.dll`, the same image as the API) with `MIGRATION_DATABASE_URL`; the migrator also gives `flow4tech_app` its LOGIN and password from `APP_DB_PASSWORD` and fails the deploy if the application credential is a superuser, has BYPASSRLS, owns tables or can run DDL. The web process never migrates.
5. **Infrastructure as code**: `railway.toml` only. `.railway/railway.ts` stays evaluated, not adopted (spec section 9, open question 1, A10).

## Consequences
- One image serves the API and the migrator; rollbacks redeploy a previous image and migrations stay expand-migrate-contract so the previous version still works against the new schema.
- Railway pre-deploy runs with the service's variables, so the owner connection string is present in the API service's environment although the web process never reads it. Accepted for the foundation; the stricter alternative (a separate migrate service holding the owner secret) is open if the threat model asks for it.
- Environment-scoped project tokens keep a leaked `staging` token from touching `production`.
- A Vercel front without a real build yet deploys a placeholder page from `vercel.json` until a UI exists.
- Railway and Vercel are hosted dependencies; the container stays standard, so moving costs a pipeline change, not a code change.

## Alternatives considered
- Other clouds (AWS, Fly.io, Render): rejected by the spec in favor of Railway for the foundation (cost and simplicity at this stage); not re-evaluated here.
- Migrations from CI against a public database URL: needs the database exposed to the internet and the owner secret in GitHub; rejected in favor of pre-deploy inside Railway's private network.
- Migrating at web app startup: the app would need the owner credential at runtime; rejected (ADR-002: the app credential never runs migrations).
