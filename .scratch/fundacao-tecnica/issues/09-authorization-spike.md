# 09: Authorization engine spike

**What to build:** Evaluate Cedar (or equivalent) against three real cases behind a single decision interface, and decide use, build or hybrid, recorded in ADR-006. Callers depend only on the interface.
**Blocked by:** 01
**Touches:** `backend/src/Kernel/Authorization/**`, `backend/src/Modules/Permissoes/**`, `backend/spikes/authz/**` (disposable), `backend/tests/Authorization/**`, `docs/adr/006-*.md`
**Nature:** objective
**tdd-scope:** required
**Seams:** Unit (decision engine), Integration (cumulative limit under concurrency)
**Status:** ready-for-agent

- [ ] A single interface `Decidir(who, resource:action, where, context)` exists; tests depend only on it
- [ ] Case 1: waiter may give discount up to X% by profile; above it is denied
- [ ] Case 2: courtesy with a daily cap; the cumulative limit is evaluated in the same transaction and two concurrent requests cannot jointly exceed the cap (concurrency test on real PostgreSQL)
- [ ] Case 3: same permission, a manager allowed only on unit 2 is denied on unit 1
- [ ] Explicit deny beats allow; default is deny; an unknown permission denies (tests)
- [ ] Comparison covers expressiveness for cumulative limits, ASP.NET policy integration, decision performance, testability (profile x permission matrix) and dependency risk; ADR-006 records use / build / hybrid
