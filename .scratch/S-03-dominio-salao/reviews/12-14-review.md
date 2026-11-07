# Review F4-0035..0037 (commits 52e9374, af6b120, 98216ab)

Verdict: PASS WITH RISKS. No blocker found. Backend payment replay is idempotent by pagamentoId (GestaoDoRecebimento.cs:125, before the sem-saldo check), so the main queue correctness holds.

## Findings (most severe first)

- risk · FecharConta.tsx:67-74,156,209-215 · After a payment is queued (`naFila`), the sheet closes and the comanda still shows the old balance. Reopening Fechar conta makes a NEW pagamentoId (reset at 73), so a partial payment can be recorded twice (queued + new); a full one gets `falhou: conta ja estava paga` after cash was taken. fix: block/redirect the sheet while `lerFila()` holds a `pagamento` for this comandaId (show "Recebimento na fila"), or subtract queued amounts. fp: spec/fechar-conta-double-queued
- risk · ComandaPage.tsx:88-110 vs fila.ts:378 · Ordering: when online, `enviar()` posts directly and bypasses the queue, so a new round can overtake an earlier queued round (or payment) of the same comanda. fix: if the queue has any non-falhou action for this comandaId, enqueue instead of sending directly. fp: spec/fila-ordering-bypass
- risk · fila.ts:72-78,141 + Shell.tsx:43 · Queue holds PII-ish data (account id, titulo with comanda name, amounts, free-text-capable observacao) in localStorage, not cleared on session expiry/forced logout/other sign-out paths; only the Shell "Sair" button calls limparFila. Other accounts' actions stay on disk (filtered but kept). fix: call limparFila inside `sair()` and on login of a different account; drop actions with another contaId at load. fp: standards/fila-localstorage-residue
- risk · fila.ts:46,72 · Two tabs: each keeps its own in-memory array and overwrites the key; an action queued in tab A can be erased by tab B's next gravar, and both tabs process (ids make it idempotent, but loss is possible). fix: listen to the `storage` event and reload, or re-read before write (merge by id). fp: spec/fila-multitab-overwrite
- risk · fila.ts:129-132 · `removerAcao` deletes a pending/enviando payment with no confirmation or ability to tell it may already have landed (in-flight). Money record silently lost. fix: confirm for tipo pagamento; refuse while `enviando`. fp: spec/fila-remove-payment
- risk · fila.ts:170-193,219 · If /fechar fails after payment succeeds, action goes `adiar` and re-POSTs the payment each retry (replay OK) but the `GET /conta` + loop run again; back-off grows to 30 s forever (no max attempts, no surfacing as stuck after N tries). fix: after payment ok persist a "pago, falta fechar" step, and mark `falhou` after N attempts. fp: spec/fila-close-retry
- note · FecharConta.tsx:157-199 · id kept across 409/400 retries while `recebido` may change; replay returns the old payment (server ignores the new amount), UI then shows server's change. Reset `pagamentoId` when digits change. fp: spec/fechar-id-vs-amount
- note · FecharConta.tsx:149-153 · "Fechar conta" (tudoPago) path is not queued offline: catch is missing for fechar(); a network throw hits `finally` with no message. fix: catch -> setErro("Sem conexao"). fp: spec/fechar-offline-no-message
- note · ComandaPage.tsx:107-112 · Undo: duration is 8 s minus elapsed since request start (conservative vs server IClock window); fine. Undo disappears on navigation; offline round queued gets no undo (ok).
- note · app.css:239 · `.caixa-resultado .f4-badge` min-height 36px (not interactive, fine). Touch targets elsewhere 44-56px. UndoToast "Desfazer" button size is set in design-system styles; not verified at 44px.
- note · queue UI: BarraDeFila `Button` toggles list; QueueList retry/remove buttons not verified for 44px targets.

## Test gaps
- No test: new tab/storage-event overwrite; payment queued then sheet reopened (double); direct send overtaking queue; fechar offline throw; 409 adoption exhausting 3 attempts (fila.test.ts:128 covers only success); sign-out via `sair()` (test calls limparFila directly, not the Shell path); removerAcao on in-flight.
- Ticket 12 items declared NOT done and honestly recorded in issue status line: last-time-at-table suggestion card, per-item note (observacao hard-coded null at ComandaPage.tsx:77,103). Acceptable, but ticket header 12 still lists them under "What to build" while Status says done; track as follow-up in 15-slice-closeout.

## Confirmed good
- Ids generated once and reused on retry (rodadaId ref; queue keeps id); version read at send time; 409 adoption bounded at 3; `falhou` kept visible and does not block; one at a time via `rodando`; account filter on send; money in integer cents via centsFromDigits; change/rest computed from cents; button `loading` guards double submit.
