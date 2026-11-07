# 20: Payment methods and mixed payment

**What to build:** Payments are no longer cash only: factory methods the organization can rename, deactivate or extend; a partial, split or mixed payment (one payer, several methods); tip; change only for cash; all through the `manual` provider.
**Blocked by:** 18
**Touches:** backend/src/Pagamentos/**, backend/src/Comandas/**, backend/src/Caixa/**, backend/migrations/**, backend/src/Api/**, backend/tests/**
**Nature:** objective
**tdd-scope:** required
**Seams:** Domain (pure); Real database (Testcontainers, app credential); Contract; Two tenants over HTTP (`DoisTenantsEndpointsTests` extended)
**Status:** ready-for-agent

- [ ] Migration for payment methods with factory defaults (cash, credit, debit, Pix, meal voucher; voucher/credit-account created inactive until phase 2B); `pagamentos` stays append-only (SELECT/INSERT only) and gains method, tip and optional split link.
- [ ] Receive pays the balance with one or more method lines in a single unit of work (idempotent by client id); cash lines count toward the shift's expected cash, other methods are recorded without cash movement.
- [ ] Change only for cash; tip is outside the balance and never reduces it; applied never exceeds the balance (existing rule).
- [ ] `Pagamentos` keeps the provider contract (`iniciar`, `confirmar`) with only `manual`; the model has a `pendente` state that nothing sets yet.
- [ ] Permissions `pagamento:dividir`, `pagamento:registrar-gorjeta`, `metodo-pagamento:gerenciar` [A]; audit; events extended; contract regenerated.
- [ ] Close the comanda still requires zero balance; reopening and reversal stay out (phase 2B).
