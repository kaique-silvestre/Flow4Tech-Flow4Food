# 19: Transfer, merge and split screens

**What to build:** The waiter transfers items, merges comandas and splits the bill with a preview, from the comanda screen.
**Blocked by:** 17, 18
**Touches:** frontend/app/src/features/comandas/**, frontend/design-system/**, frontend/app/e2e/**
**Nature:** ui
**tdd-scope:** required
**Seams:** Browser (Playwright against the real API in `docker compose`, 390x844 and 360x740) plus vitest/testing-library for logic, state and components.
**Status:** ready-for-agent

- [ ] Select items, pick a destination comanda; merge flow with confirmation; separate flow.
- [ ] Split sheet shows the five ways with the server's preview before confirming; equal split in at most 4 taps (measured).
- [ ] These operations need a connection (not queued); a conflict 409 refreshes the numbers and keeps the selection.
- [ ] Screenshots at both viewports, light/dark; states loading, error, conflict.
