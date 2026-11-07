# 25: Pre-bill and receipt screens

**What to build:** From the comanda the waiter shows or shares the pre-bill, marks the bill as requested, and later reopens the receipt of a closed comanda.
**Blocked by:** 24
**Touches:** frontend/app/src/features/comandas/**, frontend/design-system/**, frontend/app/e2e/**
**Nature:** ui
**tdd-scope:** required
**Seams:** Browser (Playwright against the real API in `docker compose`, 390x844 and 360x740) plus vitest/testing-library for logic, state and components.
**Status:** ready-for-agent

- [ ] 'Ver pré-conta' with the server's numbers; share through the phone's share sheet (fallback: copy text).
- [ ] 'Conta pedida' button with clear state; the table map shows 'aguardando conta'.
- [ ] Receipt view for a closed comanda with the second-copy permission.
- [ ] Screenshots at both viewports, light/dark; states loading, error.
