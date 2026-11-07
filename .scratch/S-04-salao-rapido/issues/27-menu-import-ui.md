# 27: Menu import screen

**What to build:** The owner uploads the file from the phone or a computer browser, reads the errors by line, confirms and sees the result.
**Blocked by:** 26
**Touches:** frontend/app/src/features/cardapio/**, frontend/app/src/router.tsx, frontend/design-system/**, frontend/app/e2e/**
**Nature:** ui
**tdd-scope:** required
**Seams:** Browser (Playwright against the real API in `docker compose`, 390x844 and 360x740) plus vitest/testing-library for logic, state and components.
**Status:** ready-for-agent

- [ ] File picker, template download, upload progress, error list by line, preview counts, confirm.
- [ ] Result summary and a link to the menu management screen; a repeat of the same import shows 'nothing to change'.
- [ ] Gated by permission; states loading, empty, error, forbidden.
- [ ] Screenshots at both viewports, light/dark.
