# 07: Receive cash and close the comanda

**What to build:** The cashier receives money for a comanda (partial or full), sees the change, and the comanda closes only when the balance is zero. A service fee (default 10%, frozen on opening) is included; removing it needs a permission and is audited. The cash received enters the open shift's expected amount. Closed comandas accept nothing more.
**Blocked by:** 03, 06
**Touches:** backend/src/Pagamentos/**, backend/src/Comandas/** (receive and close use case), backend/src/Caixa/** (public contract), backend/src/Api/** (routes), backend/migrations/0020_*.sql, backend/tests/Flow4Tech.Tests/Pagamentos*Endpoints*.cs, domain tests, catalogs, openapi.json, schema.ts
**Nature:** objective
**tdd-scope:** required
**Seams:** HTTP with real PostgreSQL; Pure domain; Architecture gates
**Status:** ready-for-agent

- [ ] Migration adds `pagamentos` (tenant table, RLS): comanda, shift, method `dinheiro` only, received, applied, change, operator, client-generated id; the app credential has only SELECT and INSERT (UPDATE/DELETE fail with 42501).
- [ ] Receive runs in ONE `IUnitOfWork.ExecuteAsync` in `Comandas.Application`: load comanda, `DecidirAsync`, call the public contract of Pagamentos (which records the movement in the shift through the public contract of Caixa), audit, emit event.
- [ ] `troco = recebido − aplicado`, never negative; applied never exceeds the balance; a partial cash payment is accepted and the response says what remains; repeating the same payment id yields one payment and the same response.
- [ ] With `caixa.exigir-caixa-aberto` on, receiving without the operator's open shift in the active unit is refused; off, the payment has no shift and counts in no shift's expected.
- [ ] Close succeeds only at balance zero and sets `fechada`; adding items or payments to a closed comanda is refused.
- [ ] Service fee: percentage `venda.taxa-servico-percentual` (number 0 to 20, organization and unit, default 10) frozen on the comanda when opened; computed on the consumption in cents with half-up rounding in one pure function; removing it needs `comanda:remover-taxa-servico`, is audited and 403 without the permission.
- [ ] Pure domain tests cover balance, fee and rounding, change and the close rule; Pagamentos exposes a provider contract (iniciar, confirmar) with a single synchronous `manual` provider, no TEF/Pix.
- [ ] Audit records `pagamento:registrar`, `comanda:fechar`, `comanda:remover-taxa-servico` and events `pagamento.registrado`, `comanda.fechada` in the same unit of work; permissions `comanda:receber-pagamento`, `comanda:fechar`, `comanda:remover-taxa-servico`, `pagamento:registrar`, `pagamento:ver` declared; contract and catalogs regenerated.
