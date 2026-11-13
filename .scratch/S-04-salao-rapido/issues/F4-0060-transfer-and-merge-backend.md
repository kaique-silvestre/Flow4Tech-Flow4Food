# F4-0060: Transfer items and merge or separate comandas

**What to build:** Items move between comandas, and comandas merge or split, atomically across two comandas without deadlock or lost items.
**Blocked by:** F4-0056
**Touches:** backend/src/Comandas/**, backend/src/Api/**, backend/tests/**
**Nature:** objective
**tdd-scope:** required
**Seams:** Domain (pure); Real database (Testcontainers, app credential); Contract; Two tenants over HTTP (`DoisTenantsEndpointsTests` extended)
**Status:** done

- [x] Transfer item(s) to another open comanda of the same unit; merge comanda B into A (B becomes closed-by-merge with no balance); separate selected items into a new comanda.
- [x] Both comandas locked in id order and both `versao` bumped; a stale version on either answers 409 with the current one.
- [x] Events `comanda.transferida`/`comanda.juntada` and audit entries on both comandas; permissions `comanda:transferir-item`, `comanda:juntar`.
- [x] Refused when either comanda is not open, in another unit/organization, or when received payments would exceed the remaining bill.
- [x] Concurrency test: two opposite transfers at once never deadlock and never lose or duplicate an item.

## Closing note
- Decisions accepted by the owner: Waiter and Manager get `comanda:transferir-item`, only the Manager gets `comanda:juntar`; `comanda.transferida` is shared with the table move (fields tell them apart); separate does not require `comanda:abrir`.
- Limits: an item with its own discount cannot be transferred; merge refuses a source with any discount or any payment.
- Full `Flow4Tech.Tests`: 690 of 690 on the second run; the first run had one intermittent failure in `ComandasEndpointsTests` that passed alone (see F4-0123).
