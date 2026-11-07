# 05: Open a comanda and list comandas

**What to build:** A waiter opens a comanda with a table number or a free name; it gets a display number (#12) that restarts each operating day; the list shows only the waiter's own comandas unless they may see others'; table status (livre/ocupada) is computed from open comandas. Every write carries and bumps a version.
**Blocked by:** 04
**Touches:** backend/src/Comandas/**, backend/src/Api/** (routes), backend/migrations/0018_*.sql, backend/tests/Flow4Tech.Tests/Comandas*Endpoints*.cs, backend/tests/**/Comanda*Tests.cs (domain), permissions/audit/settings catalogs, openapi.json, schema.ts
**Nature:** objective
**tdd-scope:** required
**Seams:** HTTP with real PostgreSQL; Pure domain; Architecture gates
**Status:** done

- [ ] Migration adds `comandas` and `comanda_numeracao` (tenant tables, RLS): unit, display number unique per unit and operating day, identification, optional `mesa_id`, channel default `salao`, responsible waiter, optional people count, state, opened_at, frozen `taxa_servico_percentual`, `versao`.
- [ ] Two simultaneous opens in the same unit and operating day get distinct, gap-free numbers (real PostgreSQL concurrency test); on the next operating day the number restarts at 1 (injected clock).
- [ ] Opening with a table already holding an open comanda is allowed (several comandas per table); with `operacao.usar-mesas` off the identification is a name or number ('Comanda 7').
- [ ] List returns only the caller's comandas without `comanda:ver-comandas-de-outros` and all of the active unit with it; never another unit or organization (two-organization test with zero crossing rows).
- [ ] Table list now returns derived status (livre, ocupada) from open comandas.
- [ ] Pure domain tests cover state transitions (aberta → fechada | cancelada rules that exist in this slice) and the numbering function with an injected clock.
- [ ] `comanda:abrir` audit record and `comanda.aberta` event in the same unit of work; stale `versao` on any write returns 409 with the current version; contract and catalogs regenerated.
