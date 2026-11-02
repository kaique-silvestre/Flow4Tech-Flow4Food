# 07: Data access choice: Dapper vs EF Core

**What to build:** Compare Dapper and EF Core on top of the RLS spike and pick one, recorded in an ADR, so Phase 1 starts with one data-access style.
**Blocked by:** 06
**Touches:** `backend/spikes/data-access/**` (disposable), `docs/adr/002-*.md`, `.scratch/fundacao-tecnica/findings/data-access.md`
**Nature:** objective
**tdd-scope:** required
**Seams:** Integration (real PostgreSQL, app credential)
**Status:** done

- [ ] Both options implement the same small use case (insert and query the sample business table) under RLS with per-transaction context
- [ ] Comparison table covers SQL clarity, typing, context-per-transaction enforcement, performance and testability, with measured numbers where relevant
- [ ] A single option is chosen and justified in ADR-002 (migration tool candidate noted: DbUp, Flyway or reviewed EF migrations)
- [ ] Spike code is clearly marked disposable; the isolation tests from ticket 06 pass with the chosen option
- [ ] Findings file lists any rule to add to `CLAUDE.md`
