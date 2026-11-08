# F4-0051: Add-ons and per-item note in the launch screen

**What to build:** In the comanda screen the waiter picks the most used add-ons in one tap, resolves required groups, and types a note per item; all of it works through the offline queue.
**Blocked by:** F4-0050, F4-0045
**Touches:** frontend/app/src/features/comandas/**, frontend/design-system/**, frontend/app/e2e/**
**Nature:** ui
**tdd-scope:** required
**Seams:** Browser (Playwright against the real API in `docker compose`, 390x844 and 360x740) plus vitest/testing-library for logic, state and components.
**Status:** ready-for-agent

- [ ] A product with required groups opens a sheet; the most used add-ons are one-tap chips; quantity by the numeric keypad.
- [ ] Per-item note field (the API already accepts it; the screen hard-codes null today).
- [ ] Queued rounds carry the chosen options by id; replay after reconnect creates one round (vitest + Playwright with the network cut).
- [ ] A product with no options still launches in 2 taps (target kept, measured).
