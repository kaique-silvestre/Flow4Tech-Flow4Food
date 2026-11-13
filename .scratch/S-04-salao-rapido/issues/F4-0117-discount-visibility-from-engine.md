# F4-0117: Show discount, courtesy and gift only to who could use them

**What to build:** Today the "Desconto" action appears for anyone who sees an open comanda and the server answers 403 when the person has no rule for it (F4-0059). Add a route that asks the authorization engine whether the session has ANY rule for discount, courtesy and gift (without values and without revealing limits), and make the comanda screen offer each action only when the answer is yes.
**Blocked by:** F4-0059
**Touches:** backend/src/Permissoes/**, backend/src/Comandas/**, backend/src/Api/**, backend/tests/**, backend/src/Api/openapi.json, frontend/packages/api-client/src/schema.ts, frontend/app/src/features/comandas/**, frontend/app/e2e/**
**Nature:** objective
**tdd-scope:** required
**Seams:** Real database (Testcontainers); Contract; Two tenants over HTTP; vitest/testing-library for the screen.
**Status:** done

- [x] Route with `.RequireSession()` and a stable `.WithName(...)`, typed response, decided by `IAuthorizationEngine` (no branching on profile name); never returns limit values.
- [x] Test for the allowed case (a profile with a rule) and the denied case (a profile without), plus another organization.
- [x] The comanda screen shows "Desconto", "Cortesia" and "Brinde" only when the route says the session has a rule; the "Mais" menu stops appearing when it would be empty; the F4-0057 test adapted in F4-0059 goes back to expecting the menu absent for who has no action.
- [x] The server's 403 stays as the last defence.
- [x] Contract regenerated and committed (openapi.json and schema.ts).
