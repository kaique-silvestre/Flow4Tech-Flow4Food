# 12: Table map screen on the phone

**What to build:** The waiter sees the salon by area, taps a free table to open a comanda or an occupied one to open it, and can move a comanda to another table.
**Blocked by:** 11
**Touches:** frontend/app/src/features/mesas/**, frontend/app/src/router.tsx, frontend/design-system/**, frontend/app/e2e/**
**Nature:** ui
**tdd-scope:** required
**Seams:** Browser (Playwright against the real API in `docker compose`, 390x844 and 360x740) plus vitest/testing-library for logic, state and components.
**Status:** ready-for-agent

- [ ] Map per area with status words (not only color) and elapsed time; tap free table = new comanda on it; tap occupied = its comandas.
- [ ] Move-to-table sheet gated by permission; 409 conflict message keeps the user's work.
- [ ] Hidden when the unit does not use tables; states loading, empty, error.
- [ ] Screenshots at both viewports, light/dark; targets at least 44px.
