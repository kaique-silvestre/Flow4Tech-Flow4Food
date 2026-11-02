# 10: Architecture test: route without declared permission

**What to build:** Every API route must declare its permission; a route without one fails the CI. Permission catalog is collected from the code of each module.
**Blocked by:** 05, 09
**Touches:** `backend/tests/Architecture/**`, `backend/src/Api/**`, `backend/src/Kernel/Authorization/**`
**Nature:** objective
**tdd-scope:** required
**Seams:** Architecture test (runs in CI), Contract (OpenAPI)
**Status:** ready-for-agent

- [ ] A route/endpoint attribute or metadata declares `resource:action` for each route
- [ ] Architecture test enumerates all routes and fails if any lacks the declaration (proven with a violating fixture route)
- [ ] Health endpoints are the only explicitly allowlisted exceptions
- [ ] Permission catalog is collected automatically from module code and exported (name list is deterministic)
- [ ] A permission unknown to the catalog is rejected at startup or by test
