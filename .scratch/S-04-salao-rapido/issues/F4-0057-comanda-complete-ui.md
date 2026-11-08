# F4-0057: Comanda edit and history screens

**What to build:** From the comanda screen a permitted user edits an item, renames the comanda, changes the waiter and people count, reads the history and cancels the comanda.
**Blocked by:** F4-0056, F4-0045
**Touches:** frontend/app/src/features/comandas/**, frontend/design-system/**, frontend/app/e2e/**
**Nature:** ui
**tdd-scope:** required
**Seams:** Browser (Playwright against the real API in `docker compose`, 390x844 and 360x740) plus vitest/testing-library for logic, state and components.
**Status:** ready-for-agent

- [ ] Item action sheet gains 'Alterar'; comanda menu gains rename, waiter, people, note, history, cancel (each shown only with its permission).
- [ ] History as a timeline list; cancel asks the standard reason and confirms before sending.
- [ ] Edits go through the queue except cancel comanda, which needs a connection (stated in the sheet).
- [ ] Screenshots at both viewports, light/dark; states loading, empty history, error, conflict 409.
