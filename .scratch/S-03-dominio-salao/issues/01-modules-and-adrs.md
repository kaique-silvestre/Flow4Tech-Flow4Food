# 01: Salon modules skeleton and ADR-014/015

**What to build:** The four new modules (Cardapio, Comandas, Pagamentos, Caixa) exist with Domain/Application/Infrastructure/Interface projects in the solution, wired to the host, with their allowed edges declared, and the two decisions behind them written as ADRs. Nothing behaves yet; the architecture gates are green with the new modules in place.
**Blocked by:** None
**Touches:** backend/Flow4Tech.sln, backend/src/{Cardapio,Comandas,Pagamentos,Caixa}/**, backend/modules.dependencies.json, backend/src/Api/Flow4Tech.Api.csproj, docs/adr/014-*.md, docs/adr/015-*.md
**Nature:** objective
**tdd-scope:** not-applicable
**Seams:** Architecture gates
**Status:** done

- [ ] Projects `Flow4Tech.<Module>.<Layer>` exist for Cardapio, Comandas, Pagamentos and Caixa and are in `Flow4Tech.sln`; Api references each module's Application project.
- [ ] `backend/modules.dependencies.json` declares exactly `Comandas → Cardapio`, `Comandas → Pagamentos`, `Pagamentos → Caixa` and no other new edge; `backend/tests/Architecture` passes (no cycles, no internals across modules).
- [ ] ADR-014 records the four modules, the three edges, why mesa lives in Comandas and why Pagamentos depends on Caixa; ADR-015 records the PWA salon app: client action queue, client-generated ids for idempotency, no full offline (D7).
- [ ] `dotnet build backend/Flow4Tech.sln` has no warnings and `dotnet format --verify-no-changes` is clean.
