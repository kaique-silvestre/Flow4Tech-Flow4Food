# F4-0063: Payment methods and mixed payment

**What to build:** Payments are no longer cash only: factory methods the organization can rename, deactivate or extend; a partial, split or mixed payment (one payer, several methods); tip; change only for cash; all through the `manual` provider.
**Blocked by:** F4-0061
**Touches:** backend/src/Pagamentos/**, backend/src/Comandas/**, backend/src/Caixa/**, backend/migrations/**, backend/src/Api/**, backend/tests/**
**Nature:** objective
**tdd-scope:** required
**Seams:** Domain (pure); Real database (Testcontainers, app credential); Contract; Two tenants over HTTP (`DoisTenantsEndpointsTests` extended)
**Status:** done

- [x] Migration for payment methods with factory defaults (cash, credit, debit, Pix, meal voucher; voucher/credit-account created inactive until phase 2B); `pagamentos` stays append-only (SELECT/INSERT only) and gains method, tip and optional split link.
- [x] Receive pays the balance with one or more method lines in a single unit of work (idempotent by client id); cash lines count toward the shift's expected cash, other methods are recorded without cash movement.
- [x] Change only for cash; tip is outside the balance and never reduces it; applied never exceeds the balance (existing rule).
- [x] `Pagamentos` keeps the provider contract (`iniciar`, `confirmar`) with only `manual`; the model has a `pendente` state that nothing sets yet.
- [x] Permissions `pagamento:dividir`, `pagamento:registrar-gorjeta`, `metodo-pagamento:gerenciar` [A]; audit; events extended; contract regenerated.
- [x] Close the comanda still requires zero balance; reopening and reversal stay out (phase 2B).

## Closing note
- Migration 0029: `metodos_pagamento` (RLS, never deleted; factory methods are created on first read or use, `vale` and `conta-credito` stay inactive and the API refuses to activate them); `pagamentos` stays append-only and gains method, tip, state (`confirmado|pendente`) and the link to a split part.
- New routes: `GET|POST /pagamentos/metodos`, `PUT /pagamentos/metodos/{id}`, `POST /comandas/{id}/recebimentos` (1 to 10 lines in one unit of work, idempotent by line id). The old `POST /comandas/{id}/pagamentos` stays on top of the same use case.
- Decisions by the agent, to confirm with the owner: Cashier gets `pagamento:dividir` and `pagamento:registrar-gorjeta`; Manager gets those and `metodo-pagamento:gerenciar`; Waiter none. A receipt pays ONE part of a split; paying a part does not invalidate the split; "a comanda that already received a payment is not split" (F4-0061) was not relaxed. The tip does not enter the shift's expected cash (only the applied cash does).
- Full `Flow4Tech.Tests`: 762 of 762. Stryker not run. Confirming a `pendente` payment is phase 2B.
