# F4-0003: Module boundary architecture tests

**What to build:** A single file declares which module may depend on which. Architecture tests fail the build on a forbidden dependency, a cycle, or access to another module's internals.
**Blocked by:** F4-0001
**Touches:** `backend/tests/Architecture/**`, `backend/modules.dependencies.*`, `CLAUDE.md`
**Nature:** objective
**tdd-scope:** required
**Seams:** Architecture test (runs in CI)
**Status:** done

- [ ] Allowed-dependencies file exists and is the only source of truth for module edges
- [ ] Test fails when a module references another module's internal type (proven by a deliberately violating fixture or temporary change)
- [ ] Test fails on a dependency cycle between modules (proven the same way)
- [ ] Test fails when domain layer references infrastructure or a framework
- [ ] `CLAUDE.md` explains how to declare a new allowed edge
