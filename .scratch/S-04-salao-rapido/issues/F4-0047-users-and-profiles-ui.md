# F4-0047: Users and profiles screen on the phone

**What to build:** The owner sees their people on the phone, creates the model profiles, assigns one to a person and sees the effect, without leaving /salao.
**Blocked by:** F4-0046
**Touches:** frontend/app/src/features/usuarios/**, frontend/app/src/router.tsx, frontend/design-system/**, frontend/app/e2e/**, backend/src/Api/** + Identidade + Permissoes read routes (GET /contas, widened autonomously, see decisoes-autonomas.md)
**Nature:** ui
**tdd-scope:** required
**Seams:** Browser (Playwright against the real API in `docker compose`, 390x844 and 360x740) plus vitest/testing-library for logic, state and components.
**Status:** done

- [ ] Screen reachable only with the profile permissions (absent, not disabled, for who lacks them); list of people with profile and unit; assign/remove a profile in a sheet; first-use shortcut 'Criar perfis modelo'.
- [ ] States: loading, empty, error, forbidden-by-server (403 shows no reason), and 'cannot change your own profile' message.
- [ ] Screenshots at both viewports, light and dark; targets at least 44px; no horizontal scroll.
- [x] Playwright against the real API: owner creates the models, assigns Garçom to a seeded person, that person signs in and sees only waiter actions. (DEFERRED: "new person" needs a create-person route; owner-only decision in .scratch/governanca/pendencias.md.)
