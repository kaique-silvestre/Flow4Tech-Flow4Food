# 08: Outbox and queue spike

**What to build:** Prove the transactional outbox: a state change and its domain event are committed atomically, a dispatcher delivers at least once, consumers are idempotent. Compare Wolverine, Hangfire and Quartz behind an own interface and choose one.
**Blocked by:** 06
**Touches:** `backend/src/Kernel/Events/**`, `backend/src/Infrastructure/Outbox/**`, `backend/spikes/queue/**` (disposable), `backend/tests/Outbox/**`, `docs/adr/003-*.md`
**Nature:** objective
**tdd-scope:** required
**Seams:** Integration (real PostgreSQL), Unit (idempotent consumer)
**Status:** done-local (pending-owner steps below)

- [ ] State change and outbox row are written in the same transaction; a forced failure between them leaves neither or both (tested)
- [ ] Dispatcher delivers each event at least once; a forced failure after delivery and before marking it done causes redelivery, not loss
- [ ] A consumer receiving the same event twice produces one observable effect (idempotency test)
- [ ] Background processing restores the organization context before touching data (tested under RLS)
- [ ] Queue sits behind an own interface; Wolverine, Hangfire and Quartz are compared and one is chosen in ADR-003
- [ ] Latency of a typical transaction (set context + insert + write outbox) is measured and recorded

## Pending-owner
Needs services that are not local; everything else in this ticket is done and proved locally.
1. Staging latency (spec 5.3 item 9): run `OUTBOX_BENCH=1 OUTBOX_BENCH_OUT=<file> dotnet test backend/tests/Outbox --filter Typical_transaction` against the staging PostgreSQL (the test uses Testcontainers; for staging point a copy of the measurement at the staging connection string, or measure from the deployed API once it writes outbox rows) and add the numbers to `docs/adr/003-outbox-e-fila.md`. Completes in ticket 12. Local loopback numbers are recorded but are not staging.
2. First CI run of the new `outbox-tests` job (needs GitHub): push the branch, open the PR, confirm `outbox-tests` is green and counted by `gates`. `actionlint` was run locally (clean).
3. Decision to confirm: Quartz chosen in ADR-003 (owner may overrule; the interface `IBackgroundJobs` makes it one adapter).
4. Follow-ups required before any production worker (ADR-003 open items F1, F2): events parked after MaxAttempts are never redelivered and nothing alerts or requeues them; the dispatcher cannot discover which organizations have pending events under RLS (needs a maintenance-role or registry decision).
