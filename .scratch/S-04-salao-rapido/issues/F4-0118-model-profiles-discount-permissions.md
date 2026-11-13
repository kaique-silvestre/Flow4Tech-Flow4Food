# F4-0118: Model profiles carry discount, courtesy and gift permissions

**What to build:** The model profiles created for a new organization include the discount, courtesy and gift permissions with sensible limits, so a fresh restaurant can use F4-0058/F4-0059 without hand-built rules. Defaults: owner and manager can use them (owner without a cap, manager with a modest percent cap and value cap), waiter cannot. The numbers are defaults the owner can edit later; state the chosen limits in the ticket closing note.
**Blocked by:** F4-0117
**Touches:** backend/src/Permissoes/**, backend/migrations/** (new file, never edit an applied one), backend/tests/Authorization/**, backend/tests/**
**Nature:** objective
**tdd-scope:** required
**Seams:** Real database (Testcontainers); Two tenants over HTTP.
**Status:** ready-for-agent

- [ ] Model profiles (new organizations and the factory policy) include the permissions; existing organizations are handled by a new migration or the documented factory path, whichever the repo already uses for profile changes.
- [ ] Authorization contract test (profile x permission matrix) updated; the waiter is still denied.
- [ ] Limits are decided by the engine from server values; nothing branches on profile name.
