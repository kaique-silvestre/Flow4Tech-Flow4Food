# 01: Contexto de organização e unidade em toda requisição

**What to build:** Middleware único que resolve `OrganizationContext` da sessão autenticada; módulo `Organizacao` com unidades e vínculo usuário-unidade; troca de unidade. Identidade mínima de teste (sessão falsa) até o ticket 03. Spec 5.1.
**Blocked by:** none
**Touches:** `backend/src/Kernel/**`, `backend/src/Organizacao/**`, `backend/src/Api/**`, `backend/migrations/**`, `backend/tests/Architecture/**`
**Nature:** objective
**tdd-scope:** required
**Seams:** Contract (OpenAPI), Architecture test, RLS
**Status:** pending

- [ ] Rota autenticada sem contexto resolvido responde 401; sem vínculo com a unidade pedida responde 403
- [ ] Contexto alimenta `TenantDatabase`/`IUnitOfWork` sem o código de negócio montá-lo
- [ ] Teste de arquitetura falha se DTO de entrada tiver campo `organizacaoId` ou `unidadeId` vindo do cliente
- [ ] Troca de unidade é operação explícita, autorizada; usuário só vê unidades com vínculo
- [ ] Dois tenants: A não enxerga unidade nem vínculo de B (credencial da aplicação)
