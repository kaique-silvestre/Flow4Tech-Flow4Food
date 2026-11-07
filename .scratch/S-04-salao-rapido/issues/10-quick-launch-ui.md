# 10: Quick launch screens: favorites, repeat, search, last-time card

**What to build:** In the comanda screen the waiter finds the next item without scrolling: favorites on top, search, 'repeat last round', and the 'last time at this table' card the vertical slice left out.
**Blocked by:** 09, 08
**Touches:** frontend/app/src/features/comandas/**, frontend/design-system/**, frontend/app/e2e/**
**Nature:** ui
**tdd-scope:** required
**Seams:** Browser (Playwright against the real API in `docker compose`, 390x844 and 360x740) plus vitest/testing-library for logic, state and components.
**Status:** ready-for-agent

- [ ] Favorites and most sold on top; search box with name/code; all keypad driven.
- [ ] 'Repetir última rodada' with a preview and the unavailable items listed; works through the queue.
- [ ] 'Última vez nesta mesa' card (reference `docs/design/refs/salao/` and prototype) appears only when the API returns something.
- [ ] Measured and written to `metas.txt`: open comanda and first item under 10 s; common item in 2 taps; repeat-round in 2 taps.
- [ ] Screenshots at both viewports, light/dark.
