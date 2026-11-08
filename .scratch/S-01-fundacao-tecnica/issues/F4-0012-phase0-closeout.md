# F4-0012: Phase 0 closeout: rollback, latency and ADRs

**What to build:** Exercise a deploy rollback once in `staging`, measure the typical transaction latency in `staging`, and finalize ADRs so Phase 1 can start.
**Blocked by:** F4-0007, F4-0008, F4-0010, F4-0011
**Touches:** `docs/adr/**`, `docs/runbooks/rollback.md`, `.scratch/S-01-fundacao-tecnica/findings/**`, `CLAUDE.md`
**Nature:** objective
**tdd-scope:** not-applicable
**Seams:** None (evidence in documents)
**Status:** ready-for-agent

- [ ] A bad release is deployed to `staging` on purpose and rolled back; steps and time are written in a rollback runbook
- [ ] Latency of a typical transaction (set context + insert + write outbox) measured in `staging` and recorded
- [ ] ADR-002, ADR-003 and ADR-006 are written and marked accepted by the owner
- [ ] Choices from 0.1/0.2 (migration tool, CI provider, secret scanner, Railway infrastructure-in-code) have ADRs
- [ ] Every acceptance box of the spec section 6 is checked with a pointer to evidence
- [ ] `CLAUDE.md` reflects the chosen data access, queue and authorization approach
