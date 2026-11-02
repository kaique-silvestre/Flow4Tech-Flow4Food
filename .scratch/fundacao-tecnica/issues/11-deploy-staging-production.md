# 11: Deploy to staging and production

**What to build:** Main branch deploys automatically to `staging`; `production` is provisioned. Railway hosts API and PostgreSQL per environment, Vercel hosts the front with branch previews. Migrations run on deploy with a credential separate from the application's.
**Blocked by:** 04, 06
**Touches:** `railway.toml` (or equivalent), `.github/workflows/**`, `frontend/**/vercel.*`, `docs/runbooks/deploy.md`
**Nature:** objective
**tdd-scope:** not-applicable
**Seams:** Pipeline run (CI), Integration (post-deploy smoke)
**Status:** ready-for-agent

- [ ] Merge to main on a green pipeline deploys to `staging` without manual steps
- [ ] `production` environment exists with its own database and variables; no production data is used in `staging`
- [ ] Environment variables are set per environment; no secret is in the repository
- [ ] Migrations run during deploy using the migration credential; the application runs with the restricted application credential (verified in `staging`)
- [ ] Healthcheck on Railway uses the readiness endpoint
- [ ] Front deploys on Vercel with a preview per branch
- [ ] Runbook documents the deploy flow
