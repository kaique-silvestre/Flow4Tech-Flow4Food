# F4-0065: Complete cash register: supply, withdrawal, per-method count, approval

**What to build:** The shift supports money in and out, a count by method, a difference tolerance with approval limited by amount, optional blind count and separation of duties.
**Blocked by:** F4-0046
**Touches:** backend/src/Caixa/**, backend/migrations/**, backend/src/Api/**, backend/tests/**
**Nature:** objective
**tdd-scope:** required
**Seams:** Domain (pure); Real database (Testcontainers, app credential); Contract; Two tenants over HTTP (`DoisTenantsEndpointsTests` extended)
**Status:** done

- [ ] Cash movements (supply/withdrawal) with reason and destination; append-only; included in expected cash: `fundo + cash received + supply - withdrawal` computed in SQL at close.
- [ ] Close with a count per method; difference inside the configured tolerance concludes alone; above it the shift is closed as pending approval.
- [ ] Approve/recount route with `caixa:aprovar-diferenca` [A][L] (amount limit through the engine); the person who closed cannot approve unless `caixa.permitir-autoaprovacao` is on; audited.
- [ ] Blind count option hides the expected value from the closer until the count is submitted; permission `caixa:fechar-cego`.
- [ ] Settings declared in the catalog: tolerance, blind count, self-approval; history route per operator and period.
- [ ] Events `caixa.suprimento`, `caixa.sangria`, `caixa.diferenca-aprovada`; permissions `caixa:suprimento`, `caixa:sangria` [A][L].
