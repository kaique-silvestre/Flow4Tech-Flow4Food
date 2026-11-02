# 08: Outbox and queue spike

**What to build:** Prove the transactional outbox: a state change and its domain event are committed atomically, a dispatcher delivers at least once, consumers are idempotent. Compare Wolverine, Hangfire and Quartz behind an own interface and choose one.
**Blocked by:** 06
**Touches:** `backend/src/Kernel/Events/**`, `backend/src/Infrastructure/Outbox/**`, `backend/spikes/queue/**` (disposable), `backend/tests/Outbox/**`, `docs/adr/003-*.md`
**Nature:** objective
**tdd-scope:** required
**Seams:** Integration (real PostgreSQL), Unit (idempotent consumer)
**Status:** ready-for-agent

- [ ] State change and outbox row are written in the same transaction; a forced failure between them leaves neither or both (tested)
- [ ] Dispatcher delivers each event at least once; a forced failure after delivery and before marking it done causes redelivery, not loss
- [ ] A consumer receiving the same event twice produces one observable effect (idempotency test)
- [ ] Background processing restores the organization context before touching data (tested under RLS)
- [ ] Queue sits behind an own interface; Wolverine, Hangfire and Quartz are compared and one is chosen in ADR-003
- [ ] Latency of a typical transaction (set context + insert + write outbox) is measured and recorded
