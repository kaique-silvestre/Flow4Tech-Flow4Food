# 15: Discount, courtesy and gift with limits decided by the engine

**What to build:** A permitted person gives a discount (percent or value, on an item or the comanda), a courtesy or a gift; the amount or percent is computed on the server from the stored comanda and the engine enforces per-profile limits, including the cumulative daily cap.
**Blocked by:** 13, 03
**Touches:** backend/src/Comandas/**, backend/src/Permissoes/**, backend/migrations/**, backend/src/Api/**, backend/tests/Authorization/**, backend/tests/**
**Nature:** objective
**tdd-scope:** required
**Seams:** Domain (pure); Real database (Testcontainers, app credential); Contract; Two tenants over HTTP (`DoisTenantsEndpointsTests` extended)
**Status:** ready-for-agent

- [ ] Items get a type (`normal`, `cortesia`, `brinde`); courtesy/gift charge zero but keep their reason and original price for reports; distinct events `comanda.item-cortesia` and `comanda.item-brinde` carry what the future stock write-off needs.
- [ ] Discount recorded as its own rows (item or comanda scope, percent or value), blocked from pushing the total below zero; calculation order fixed in ADR-018 (consumption, item discount, comanda discount, service fee on the discounted consumption, tip outside the balance).
- [ ] Each action calls `DecidirAsync` inside the unit of work with `Value`/`Percent` taken from the server data; above the limit = 403 without reason; the cap reservation rolls back with the operation; denial audited (`sensitive`).
- [ ] Permissions `comanda:desconto`, `comanda:cortesia`, `comanda:brinde` [A][L] in the catalog and in the authorization contract matrix; limits configurable per profile through the persisted rules.
- [ ] Property tests: total never negative, rounding half-up in one pure function; concurrency test on the daily cap through HTTP.
- [ ] ADR-018 written (calculation order and rounding).
