# Review F4-0029 (06) and F4-0030 (07)

Verdict: PASS WITH RISKS (read-only review, tests not run: no Docker check made)

No block findings. Criteria of both tickets map to code and tests (409 stale versao over HTTP is covered in RodadasEndpointsTests:286 and PagamentosEndpointsTests:240; idempotent ids, undo window, fee half-up, below-received refusals, 42501 immutability, rollback tests present). Version bump runs before the writes and takes the row lock, so the balance read before the lock is safe under READ COMMITTED.

- risk · spec/security · GestaoDoRecebimento.cs:70,89 vs :119,181,243 · Bill and payment list hide a comanda the caller did not open (needs comanda:ver-comandas-de-outros), but receive, close and remove-fee only check the route permission and the unit. A cashier without that permission can pay and close a bill they cannot read (blind money write). fix: apply the same visibility rule in the three writes, or document that cashier profiles must hold ver-comandas-de-outros, and add a test with a cashier lacking it. fp: spec/recebimento/visibility-asymmetry
- risk · spec · GestaoDeRodadas.cs:213 · Undo needs the exact current versao; a second device launching inside the 8 s window makes the sender's undo 409. Correct by design but it erodes the undo promise. fix: confirm with product; if undo should not depend on versao, bump without comparing. fp: spec/rodadas/undo-needs-versao
- note · standards · tests · The race branches (GestaoDeRodadas.cs:127-129, GestaoDoRecebimento.cs:149-150: concurrent SAME round/payment id) are untested; the concurrency tests use different ids. fix: add a parallel same-id test (one 201, rest 200, one row). fp: standards/tests/same-id-race
- note · spec · GestaoDoRecebimento.cs:255 · A comanda with no consumption (balance 0) closes. Plausible; confirm it is wanted. fp: spec/fechar/empty-comanda
- note · spec · GestaoDeRodadas.cs:198 · A round with any cancelled item can no longer be undone, which the ticket does not state (it is tested, so intended). fp: spec/rodadas/undo-after-cancel
- note · spec · GestaoDoRecebimento.cs:228 · Fee removal writes audit but no outbox event; ticket asks only audit. Fine.

Money math checked: fee Math.Round AwayFromZero on decimal (Comanda.cs:82); aplicado = min(recebido, saldo); troco = recebido - aplicado with DB CHECK recebido = aplicado + troco; SemSaldo refused; shift expected computed in SQL at close under the row lock. Cross-org/unit: org and unit only from the session, RLS plus unit filter in AcharAsync; no client-supplied context.
