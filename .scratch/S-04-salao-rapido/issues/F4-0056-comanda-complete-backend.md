# F4-0056: Complete comanda I: edit items, rename, change waiter, people, history, cancel

**What to build:** Everything a waiter or manager changes on a comanda without touching money: item quantity/note, comanda name, responsible waiter, number of people, a readable history, and cancelling a comanda that has no payments.
**Blocked by:** F4-0044
**Touches:** backend/src/Comandas/**, backend/migrations/**, backend/src/Api/**, backend/tests/**
**Nature:** objective
**tdd-scope:** required
**Seams:** Domain (pure); Real database (Testcontainers, app credential); Contract; Two tenants over HTTP (`DoisTenantsEndpointsTests` extended)
**Status:** ready-for-agent

- [ ] Alter item (quantity, note) with `comanda:alterar-item`; quantity down below received is refused as cancel already is; frozen price unchanged.
- [ ] Rename, change waiter, number of people, general note: each with its permission, audited, `versao` checked.
- [ ] History route: timeline of the comanda's events (who, what, when) read from the audit/outbox data without exposing secrets; permission `comanda:ver-historico`.
- [ ] Cancel comanda: only with zero payments, with a standard reason; event `comanda.cancelada`; all open items become cancelled with the same reason.
- [ ] A comanda in the middle of closing refuses new launches (lock flag set by the close attempt, released on failure); concurrency test.
