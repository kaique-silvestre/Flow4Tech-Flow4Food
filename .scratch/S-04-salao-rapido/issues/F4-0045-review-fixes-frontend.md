# F4-0045: Frontend and deploy fixes from the independent reviews of tickets 08 to 14

**What to build:** Fix the waiter-phone risks found by the reviews: no double payment, queue order respected, nothing of one account left for the next on a shared phone.
**Blocked by:** None
**Touches:** frontend/app/src/**, frontend/app/vercel.json, docs/runbooks/deploy.md, frontend/app/e2e/**
**Nature:** ui
**tdd-scope:** required
**Seams:** Browser (Playwright against the real API in `docker compose`, 390x844 and 360x740) plus vitest/testing-library for logic, state and components.
**Status:** ready-for-agent

- [ ] While a payment is queued for a comanda, the close sheet either shows the balance minus the queued amount or is blocked with a clear message; reopening never creates a second payment id for the same amount (vitest + Playwright with the network cut).
- [ ] When the queue holds a live action for a comanda, a new round or item goes through the queue even if the network is up, so it cannot overtake an earlier queued action.
- [ ] Every sign-out path (button, expired session, 401 after refresh) clears the offline queue and the whole TanStack Query cache; entries of other accounts found on load are dropped. Test through the real Shell button and through an expired session.
- [ ] Two tabs of the same account do not erase each other's queued actions (merge by id on write, `storage` event); vitest.
- [ ] Removing a pending or in-flight payment from the queue asks for confirmation and is refused while it is sending; a close that fails after its payment succeeded stops retrying after a bounded number of attempts and shows as stuck.
- [ ] `vercel.json` routes the API paths to the API before the catch-all (or the runbook states the chosen alternative with its CORS/cookie plan); the generated-type casts in the cash register screen are removed if the routes now return typed responses.
- [ ] No new flaky browser test: Playwright suite green at both viewports.
