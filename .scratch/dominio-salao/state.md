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
- 08 · frontend-harness · done · rounds: 1 · review: pending · notes: vite preview serves the production bundle (real service worker) and proxies API paths; Playwright needs PW_CHROMIUM in cloud sessions; the compose `seed` service and the new CI jobs (front tests, browser tests) were NOT run end to end here (the e2e seed tool and the Playwright suite were run against a local PostgreSQL and the API); DESIGN.md approval still pending (screenshots in frontend/app/e2e-results/screenshots)
- 09 · login-password-unit-ui · done · rounds: 1 · review: pending · AWAITING OWNER APPROVAL of the visuals (docs/design/refs/salao/autoral/*.png, 390x844; 360x740 in frontend/app/e2e-results/screenshots after a run) · notes: the organization has no short code, the operational login asks for its id (saved on the device); permission-based hiding of /salao (no comanda:ver) is not done because no route tells the client its permissions
- 10 · cash-register-ui · done · rounds: 1 · review: pending · notes: added GET /sessao/permissoes (own salao permissions, read-only) so the app HIDES actions/tabs by permission; the open-shift card shows float + received + expected (ticket), the close sheet asks the count without repeating it; shortage = alert marker, surplus = warning marker
- 11 · comanda-list-and-new-ui · pending · blocked_by: 04,05,09
- 12 · comanda-launch-ui · pending · blocked_by: 06,11
- 13 · offline-queue-and-conflict-ui · pending · blocked_by: 12
- 14 · close-bill-cash-ui · pending · blocked_by: 07,10,12
- 15 · slice-closeout · pending · blocked_by: 13,14
lessons: 0
