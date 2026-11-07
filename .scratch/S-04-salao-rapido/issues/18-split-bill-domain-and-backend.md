# 18: Split the bill: five ways with deterministic cents

**What to build:** Given a comanda the server computes a split preview and records the chosen split: equal parts, by person/seat, by item, free values, by percent; shared charges follow the chosen rule.
**Blocked by:** 15
**Touches:** backend/src/Comandas/Domain/**, backend/src/Comandas/Application/**, backend/src/Api/**, backend/tests/**
**Nature:** objective
**tdd-scope:** required
**Seams:** Domain (pure); Real database (Testcontainers, app credential); Contract; Two tenants over HTTP (`DoisTenantsEndpointsTests` extended)
**Status:** ready-for-agent

- [ ] One pure function for equal split with the remainder cents going to the first parts in a stable order; property tests: sum of parts equals the total, no negative part, deterministic, parts differ by at most one cent.
- [ ] By item / by seat (item has an optional seat), free values (must sum to the balance) and percent (must sum to 100, cents resolved by the same rule).
- [ ] Preview route is read-only and idempotent; recording a split is idempotent by client id and stored as `divisoes` rows that later payments reference; changing the comanda after a split invalidates it (clear 409/422).
- [ ] Permission `comanda:dividir`; audit; event `comanda.dividida`; contract regenerated.
- [ ] Service fee and tip follow the chosen rule and are part of the sums.
