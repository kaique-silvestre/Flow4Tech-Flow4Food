# F4-0037: Screen: close the bill in cash with change

**What to build:** The waiter or cashier closes the bill from the comanda: consumption, service fee (removable with permission), total, amount received in cash, change in evidence, partial payment, and the comanda becomes Fechada.
**Blocked by:** F4-0030, F4-0033, F4-0035
**Touches:** frontend/app/src/features/comandas/** (close sheet), frontend/design-system/**, e2e specs
**Nature:** ui
**tdd-scope:** required
**Seams:** UI in the browser
**Status:** ready-for-agent

- [ ] 'Fechar conta' opens the sheet with the structure of `05.png`: consumption, service fee line with its toggle (shown only to who may remove it), total, 'Valor a receber · restará R$ x', and ONE button 'Dinheiro' (no Pix, credit, debit, voucher or split buttons are rendered).
- [ ] Entering the cash received shows the change in large type; an amount lower than the balance becomes a partial payment and the comanda stays open with what remains; the payment carries a client-generated id and goes through the offline queue.
- [ ] With the cash register closed the sheet says 'Abra o caixa para receber' with a link to the caixa screen; success marks the comanda Fechada and returns to the list.
- [ ] Money is shown in BRL with tabular figures and never from a float; the balance shown matches the server's.
- [ ] Screenshots of full cash payment with change, partial payment, cash register closed, service fee removed, and error at both viewports; no horizontal scroll; targets at least 44px.
- [ ] Playwright against the real API: open shift, open comanda, launch, send, close in cash with change, close the shift and see the difference in words.
