# F4-0046: Model profiles (waiter, cashier, manager) and routes to assign them

**What to build:** The owner can create the three model profiles in one call and assign a profile to a person, through HTTP routes that use the existing `GestaoDePerfis` service and its anti-self-promotion and no-escalation guards.
**Blocked by:** F4-0044
**Touches:** backend/src/Permissoes/**, backend/src/Api/**, backend/tests/Authorization/**, backend/permissions.catalog.txt, backend/audit.catalog.txt
**Nature:** objective
**tdd-scope:** required
**Seams:** Domain (pure); Real database (Testcontainers, app credential); Contract; Two tenants over HTTP (`DoisTenantsEndpointsTests` extended)
**Status:** done

- [ ] Routes (named, typed, session + permission): create profiles from models, list profiles, list a person's profiles, assign and remove an assignment, set a person's overrides; permissions `perfil:gerir`, `perfil:ver`, `permissao:atribuir` are asked by the routes and the catalog is regenerated.
- [ ] Models give waiter, cashier and manager the permissions of the current e2e seed profiles plus the new ones of this phase as they land; the model definition is data, not branching on a profile name (`ProfileBranchingTests` stays green).
- [ ] Assigning to oneself, handing on more than one holds, or a unit of another organization is refused as before (`ResultadoGestao` mapped to 403 without reason / 400); each refusal audited.
- [ ] Every change bumps the affected accounts' credential version, ending their sessions on the next request (existing trigger), proven over HTTP.
- [ ] Matrix test for each new permission; two-tenant test: a profile of organization A is invisible and unassignable in B.
