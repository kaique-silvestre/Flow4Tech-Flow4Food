# F4-0062: Transfer, merge and split screens

**What to build:** The waiter transfers items, merges comandas and splits the bill with a preview, from the comanda screen.
**Blocked by:** F4-0060, F4-0061
**Touches:** frontend/app/src/features/comandas/**, frontend/design-system/**, frontend/app/e2e/**
**Nature:** ui
**tdd-scope:** required
**Seams:** Browser (Playwright against the real API in `docker compose`, 390x844 and 360x740) plus vitest/testing-library for logic, state and components.
**Status:** done

- [x] Select items, pick a destination comanda; merge flow with confirmation; separate flow.
- [x] Split sheet shows the five ways with the server's preview before confirming; equal split in at most 4 taps (measured).
- [x] These operations need a connection (not queued); a conflict 409 refreshes the numbers and keeps the selection.
- [x] Screenshots at both viewports, light/dark; states loading, error, conflict.

## Closing note
- Equal split takes 4 taps from the comanda screen (measured in e2e). Design review PASS on the second round.
- Limits: the "by seat" mode only works with items launched with a seat (the launch screen does not offer the seat yet); without seats the sheet suggests "by item". The tip is always sent as 0; there is no screen to review a split after it is made (`GET /divisao` exists).
- Open: the JS bundle is 212.0 kB gzip against a 210 kB budget; the owner decides between raising the limit and route-level code splitting (F4-0111) before this is pushed.
