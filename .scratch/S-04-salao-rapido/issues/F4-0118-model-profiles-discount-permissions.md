# F4-0118: Model profiles carry discount, courtesy and gift permissions

**What to build:** The model profiles created for a new organization include the discount, courtesy and gift permissions with sensible limits, so a fresh restaurant can use F4-0058/F4-0059 without hand-built rules. Defaults: owner and manager can use them (owner without a cap, manager with a modest percent cap and value cap), waiter cannot. The numbers are defaults the owner can edit later; state the chosen limits in the ticket closing note.
**Blocked by:** F4-0117
**Touches:** backend/src/Permissoes/**, backend/migrations/** (new file, never edit an applied one), backend/tests/Authorization/**, backend/tests/**
**Nature:** objective
**tdd-scope:** required
**Seams:** Real database (Testcontainers); Two tenants over HTTP.
**Status:** done

- [x] Model profiles (new organizations and the factory policy) include the permissions; existing organizations are handled by a new migration or the documented factory path, whichever the repo already uses for profile changes.
- [x] Authorization contract test (profile x permission matrix) updated; the waiter is still denied.
- [x] Limits are decided by the engine from server values; nothing branches on profile name.

## Closing note
- Model profiles are created on demand by `POST /perfis/modelos` (`ModelosDePerfil`), not with the organization, and the factory `dono` already has the whole catalogue without a cap; the gap was the Manager. No migration (F4-0056/F4-0057 changed the models the same way).
- Chosen limits (Manager): discount up to 20% and R$ 150,00 per discount; courtesy and gift up to R$ 80,00 of list value, no percent cap (the engine measures them at 100%); no daily cap. Waiter and Cashier get none of the three.
- Organizations that already created the Manager profile are not updated; they edit the profile. A migration by profile name would branch on the name, so it was left out.
