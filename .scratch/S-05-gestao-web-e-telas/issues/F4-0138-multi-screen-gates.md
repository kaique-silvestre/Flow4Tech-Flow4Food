# F4-0138: Gates and captures for the four resolutions

**What to build:** block R5 of slice S-05 (see the spec).
**Blocked by:** F4-0137
**Touches:** frontend/app/playwright.config.ts, frontend/app/e2e/**, docs/agents/ci-gates.md, frontend/app/src/resolucoes-e2e.test.ts, docs/design/refs/**
**Nature:** objective
**tdd-scope:** required
**Status:** done

- [x] Playwright projects for 390x844, 360x740, 820x1180 and 1440x900 run the critical flows and the captures in both themes.
- [x] axe runs on the three breakpoints; bundle budget still green; dependency-cruiser and Knip unchanged.
- [~] Approved captures are stored under docs/design/refs/ (partial: the phone only has list and comanda captures, light, at 390; 360 and dark phone captures are left for later).
- [ ] CI job time stays within the current budget: pending, to be measured on the first CI run after the push (local e2e 19.5 s -> 27.4 s).
