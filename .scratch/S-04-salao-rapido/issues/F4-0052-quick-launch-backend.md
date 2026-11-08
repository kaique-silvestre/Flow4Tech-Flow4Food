# F4-0052: Quick launch data: favorites, repeat last round, search by name or code, last time at the table

**What to build:** The API answers what makes launching fast: the waiter's favorites, a 'repeat the last round' operation, search by name or short code, and what this table ordered last time.
**Blocked by:** None
**Touches:** backend/src/Cardapio/**, backend/src/Comandas/**, backend/src/Api/**, backend/tests/**
**Nature:** objective
**tdd-scope:** required
**Seams:** Real database (Testcontainers, app credential); Contract; Two tenants over HTTP (`DoisTenantsEndpointsTests` extended)
**Status:** ready-for-agent

- [ ] Favorites per waiter derived from `produtos_frequencia` (already fed on launch) with a stable order; list route.
- [ ] 'Repeat last round' creates a new round from the previous one of the same comanda (idempotent by client id, prices re-read, inactive or 'acabou' items reported, not silently dropped).
- [ ] Search by name (accent- and case-insensitive) and exact short code; capped, ordered, no N+1.
- [ ] 'Last time at this table' returns the previous closed comanda's items for the table in the same organization and unit only; empty when none; permission-gated.
- [ ] Contract regenerated; two-tenant test that nothing crosses organizations.
