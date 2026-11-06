base: claude/flow4tech-fase-1-handoff-vgu3m9 @ 42f834d
mode: auto   max_parallel: 1 (sequential, shared catalogs; no worktrees)
wave: sequential 01..15
tickets:
- 01 · modules-and-adrs · done · 47effea · rounds: 1
- 02 · menu-minimum · done · 46ab960 · rounds: 1 · risks: no pure-domain test, no ToQueryString test, unique race 500 (23505), price-freeze unproved until 06
- 03 · cash-shift · done · 96479e7 · rounds: 1 · risks: body fields missing default to 0 (close count), no rollback-atomicity test; fix in 07
- 04 · tables · done · 3b7c2a9 · rounds: 1 · note: base run had 2-3 load-induced flaky failures (health, port binding, latency); pass in isolation
- 05 · open-comanda-and-list · done · d9e9571 + follow-up · rounds: 2 · risks: stale-versao 409 unproved over HTTP until 06 (06 MUST add the HTTP 409 test), domain ProximoNumero/PodeTransitar unused in prod (numbering proved by HTTP tests), list capped at 200 and open-only, POST /comandas not idempotent
- 06 · rounds-and-items · done · rounds: 1 · review: pending (independent reviewer not yet run) · notes: undo of a round by a non-owner waiter answers 404 (comanda invisible); owner/other with ver-comandas-de-outros gets 409 somente-quem-enviou; GET /comandas/{id}/itens added; produtos_frequencia now fed on launch
- 07 · cash-payment-and-close · done · rounds: 1 · review: pending (no independent reviewer run on 06/07) · notes: shift expected is computed in SQL at close (fundo + recebido_dinheiro) so a racing payment is never missed; cancel item/undo round/remove fee are refused (409 valor-ja-recebido) when the bill would drop below what was received; pagamento.registrado/comanda.fechada events; 03 risks fixed here: absent amount fields on open/close shift are now 400, and shift-close rollback atomicity is tested
- 08 · frontend-harness · pending · blocked_by: 01
- 09 · login-password-unit-ui · pending · blocked_by: 08
- 10 · cash-register-ui · pending · blocked_by: 03,09
- 11 · comanda-list-and-new-ui · pending · blocked_by: 04,05,09
- 12 · comanda-launch-ui · pending · blocked_by: 06,11
- 13 · offline-queue-and-conflict-ui · pending · blocked_by: 12
- 14 · close-bill-cash-ui · pending · blocked_by: 07,10,12
- 15 · slice-closeout · pending · blocked_by: 13,14
lessons: 0
