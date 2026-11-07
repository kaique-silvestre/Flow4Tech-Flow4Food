# 23: Complete cash register screens

**What to build:** The cash register screen gets supply, withdrawal, the count by method, blind count and the pending-approval flow for managers.
**Blocked by:** 22
**Touches:** frontend/app/src/features/caixa/**, frontend/design-system/**, frontend/app/e2e/**
**Nature:** ui
**tdd-scope:** required
**Seams:** Browser (Playwright against the real API in `docker compose`, 390x844 and 360x740) plus vitest/testing-library for logic, state and components.
**Status:** ready-for-agent

- [ ] Supply and withdrawal sheets with reason and destination; movement list of the shift.
- [ ] Close sheet with a count per method; difference in words; blind mode hides the expected value until submitted.
- [ ] Manager view of pending differences with approve/recount, gated by permission.
- [ ] Screenshots at both viewports, light/dark; states closed, open, pending, error.
