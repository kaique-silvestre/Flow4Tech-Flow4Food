# F4-0119: Decimal percent in the discount keypad

**What to build:** The discount keypad accepts up to two decimals in percent mode (e.g. 7,5%), matching what the backend already accepts; the preview and the request use the same value.
**Blocked by:** F4-0117
**Touches:** frontend/app/src/features/comandas/**, frontend/app/e2e/**
**Nature:** ui
**tdd-scope:** required
**Seams:** vitest/testing-library for logic and component; Playwright at 390x844 and 360x740.
**Status:** ready-for-agent

- [ ] Percent mode accepts a decimal separator and at most two decimals, range above 0 and up to 100; the value mode is unchanged.
- [ ] Preview math (ADR-018 order and rounding) is correct for decimal percents; tests cover rounding edges.
- [ ] Request sends the decimal percent as the contract types it.
