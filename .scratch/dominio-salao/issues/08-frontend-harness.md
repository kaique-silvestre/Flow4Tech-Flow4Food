# 08: Front-end harness for /salao: PWA, tests, design-system base

**What to build:** The `app` package serves the `/salao` route as an installable PWA, with the generated API client, vitest + testing-library and Playwright running against the real API in `docker compose` with seeded test data, and the minimal design-system components built from the DESIGN.md tokens. Nothing is a user flow yet beyond a rendered shell.
**Blocked by:** 01
**Touches:** frontend/app/**, frontend/design-system/**, frontend/packages/api-client/** (consume only), frontend/package.json, pnpm-workspace.yaml, pnpm-lock.yaml, docker-compose.yml (e2e profile/seed), .github/workflows/ci.yml (front test job), docs/agents/project.md
**Nature:** objective
**tdd-scope:** required
**Seams:** UI in the browser
**Status:** done

- [x] `frontend/app` is a Vite + React + TypeScript SPA with TanStack Router/Query, route `/salao`, a web manifest and a service worker making it installable; `pnpm -r typecheck` passes under `frontend/tsconfig.base.json` (strict).
- [x] `pnpm -r --if-present test` runs vitest + testing-library and passes; a Playwright setup runs at 390x844 and 360x740 against `docker compose` with a documented seed (two organizations, an owner, an operational waiter with a custom profile created through `GestaoDePerfis`, a unit, a few products) and writes screenshots to disk.
- [x] `frontend/design-system` exports tokens (colors in OKLCH for light and dark, type, space, radius, motion) from `docs/design/DESIGN.md` and the minimal components used by the slice: button (primary, secondary), chip, tabs, bottom sheet, keypad, item row, status badge; each has default, hover, focus-visible, active, disabled, loading and error states and a vitest test; font files are self-hosted.
- [x] A CI job runs the front tests; the commands are added to `docs/agents/project.md` (dev server, vitest, Playwright, e2e).
- [x] No horizontal scroll at 360px and touch targets of at least 44px in the shell.
