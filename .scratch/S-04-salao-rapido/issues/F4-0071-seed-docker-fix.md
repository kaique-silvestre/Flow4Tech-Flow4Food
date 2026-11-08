# F4-0071: Fix the e2e seed container (missing libgssapi_krb5)

**What to build:** `docker compose --profile e2e run --rm seed` works without the host's dotnet, as the project docs already say it does.
**Blocked by:** None
**Touches:** Dockerfile, docker-compose.yml, docs/agents/project.md
**Nature:** objective
**tdd-scope:** required
**Seams:** Real database (Testcontainers, app credential)
**Status:** ready-for-agent

- [ ] Root cause confirmed (the `aspnet` runtime image lacks `libgssapi_krb5.so.2` for Npgsql) and fixed in the `e2e-seed` stage only (never in the deployed image) or by disabling GSS in the connection string.
- [ ] From a clean state `docker compose up --build api` then the seed command exits 0 and writes `seed.json`; running it twice stays idempotent.
- [ ] `docs/agents/project.md` unchanged in meaning or corrected if it was wrong.
