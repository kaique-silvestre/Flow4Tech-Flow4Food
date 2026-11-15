# F4-0135: Adaptive shell and context header

**What to build:** block R2 of slice S-05 (see the spec).
**Blocked by:** F4-0134
**Touches:** frontend/app/src/Shell.tsx, frontend/design-system/**, frontend/app/src/app.css, frontend/app/bundle-budget.json
**Nature:** ui
**tdd-scope:** required
**Status:** ready-for-agent

- [ ] Navigation is bottom tabs on the phone, a rail on the tablet and a sidebar on the computer, always filtered by the authorization engine.
- [ ] Header shows organization, unit, shift, user and the theme switch, read from the session (never from the client).
- [ ] Vitest for the breakpoint logic and the permission filter; Playwright captures at 390x844, 820x1180 and 1440x900, light and dark, reviewed by the design-reviewer against the prototype.
