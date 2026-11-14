# F4-0064: Receive with several methods on the phone

**What to build:** The close-the-bill sheet offers the active methods, takes one or several lines, shows change for cash and tip, and can follow a split.
**Blocked by:** F4-0063, F4-0045
**Touches:** frontend/app/src/features/comandas/**, frontend/design-system/**, frontend/app/e2e/**
**Nature:** ui
**tdd-scope:** required
**Seams:** Browser (Playwright against the real API in `docker compose`, 390x844 and 360x740) plus vitest/testing-library for logic, state and components.
**Status:** done

- [ ] Method chips from the API (no hard-coded list); one line per method; remaining balance live; change in large type for cash.
- [ ] Partial and mixed payments leave the comanda open with what remains; payments still carry client ids and respect the fixes of ticket 02 (no double payment).
- [ ] Receiving with a split shows whose part is being paid.
- [ ] Mixed payment of two methods in at most 8 taps (measured); screenshots at both viewports, light/dark; states cash register closed, error, offline-queued.
