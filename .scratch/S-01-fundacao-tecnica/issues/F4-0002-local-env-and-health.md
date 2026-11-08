# F4-0002: Local environment and health endpoints

**What to build:** One command brings up PostgreSQL and the API in containers locally and another runs the whole test suite. The API exposes liveness and readiness endpoints; readiness checks the database. This is the base for every integration test and for the Railway healthcheck.
**Blocked by:** F4-0001
**Touches:** `docker-compose*.yml`, `Dockerfile`, `backend/src/Api/**`, `backend/tests/**`, `CLAUDE.md`
**Nature:** objective
**tdd-scope:** required
**Seams:** Integration (real PostgreSQL via containers)
**Status:** done

- [ ] Single documented command starts PostgreSQL and the API locally
- [ ] Single documented command runs all tests
- [ ] `GET` liveness returns OK without touching the database
- [ ] `GET` readiness returns OK only when the database answers, and a failing status when it does not (tested)
- [ ] Container image is a standard Linux image with configuration by environment variables only
- [ ] `CLAUDE.md` documents the real commands
