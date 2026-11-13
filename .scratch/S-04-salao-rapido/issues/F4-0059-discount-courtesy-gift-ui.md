# F4-0059: Discount, courtesy and gift screens

**What to build:** In the comanda screen a permitted person applies a discount, courtesy or gift with a reason, sees the new total, and is told plainly when the limit blocks it.
**Blocked by:** F4-0058
**Touches:** frontend/app/src/features/comandas/**, frontend/design-system/**, frontend/app/e2e/**
**Nature:** ui
**tdd-scope:** required
**Seams:** Browser (Playwright against the real API in `docker compose`, 390x844 and 360x740) plus vitest/testing-library for logic, state and components.
**Status:** done

- [x] Action sheet per item and for the comanda: percent/value keypad, mandatory reason list, preview of the new total from the server's numbers.
- [x] These operations need a connection (not queued); offline state explains it; a 403 shows 'sem permissão para este valor' without the limit value.
- [x] Items marked as courtesy/gift are visible as such in the comanda and the bill.
- [x] Screenshots at both viewports, light/dark; states loading, error, denied.
