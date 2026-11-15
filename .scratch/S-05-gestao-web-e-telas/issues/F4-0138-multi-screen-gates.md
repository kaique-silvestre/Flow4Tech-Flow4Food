# F4-0138: Gates and captures for the four resolutions

**What to build:** block R5 of slice S-05 (see the spec).
**Blocked by:** F4-0137
**Touches:** frontend/app/playwright.config.ts, frontend/app/e2e/**, docs/agents/ci-gates.md
**Nature:** objective
**tdd-scope:** required
**Status:** ready-for-agent

- [ ] Playwright projects for 390x844, 360x740, 820x1180 and 1440x900 run the critical flows and the captures in both themes.
- [ ] axe runs on the three breakpoints; bundle budget still green; dependency-cruiser and Knip unchanged.
- [ ] Approved captures are stored under docs/design/refs/ and the CI job time stays within the current budget (list what was cut if not).
