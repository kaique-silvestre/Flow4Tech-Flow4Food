# 06: Menu management screen on the phone

**What to build:** Whoever may manage the menu can toggle 'acabou', activate/deactivate and change a price from the phone in a few taps; the waiter's product grid reflects it.
**Blocked by:** 05
**Touches:** frontend/app/src/features/cardapio/**, frontend/app/src/router.tsx, frontend/design-system/**, frontend/app/e2e/**
**Nature:** ui
**tdd-scope:** required
**Seams:** Browser (Playwright against the real API in `docker compose`, 390x844 and 360x740) plus vitest/testing-library for logic, state and components.
**Status:** ready-for-agent

- [ ] List by category/subcategory with search; per product: 'acabou' switch, active switch, price sheet (with promotional price and window) gated by permission.
- [ ] Price change above the permission's limit shows the generic denied message (server decides); no reason leaked.
- [ ] A product marked 'acabou' disappears from (or is clearly marked in) the launch grid on the next refresh; vitest + Playwright.
- [ ] Screenshots at both viewports, light/dark; states loading, empty, error, forbidden.
