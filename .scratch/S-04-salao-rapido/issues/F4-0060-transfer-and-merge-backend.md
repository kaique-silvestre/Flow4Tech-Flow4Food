# F4-0060: Transfer items and merge or separate comandas

**What to build:** Items move between comandas, and comandas merge or split, atomically across two comandas without deadlock or lost items.
**Blocked by:** F4-0056
**Touches:** backend/src/Comandas/**, backend/src/Api/**, backend/tests/**
**Nature:** objective
**tdd-scope:** required
**Seams:** Domain (pure); Real database (Testcontainers, app credential); Contract; Two tenants over HTTP (`DoisTenantsEndpointsTests` extended)
**Status:** ready-for-agent

- [ ] Transfer item(s) to another open comanda of the same unit; merge comanda B into A (B becomes closed-by-merge with no balance); separate selected items into a new comanda.
- [ ] Both comandas locked in id order and both `versao` bumped; a stale version on either answers 409 with the current one.
- [ ] Events `comanda.transferida`/`comanda.juntada` and audit entries on both comandas; permissions `comanda:transferir-item`, `comanda:juntar`.
- [ ] Refused when either comanda is not open, in another unit/organization, or when received payments would exceed the remaining bill.
- [ ] Concurrency test: two opposite transfers at once never deadlock and never lose or duplicate an item.
