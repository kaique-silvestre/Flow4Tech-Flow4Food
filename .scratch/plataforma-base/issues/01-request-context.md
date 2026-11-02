# 01: Contexto de organização e unidade em toda requisição

**What to build:** Middleware único que resolve `OrganizationContext` da sessão autenticada; módulo `Organizacao` com unidades e vínculo usuário-unidade; troca de unidade. Identidade mínima de teste (sessão falsa) até o ticket 03. Spec 5.1.
**Blocked by:** none
**Touches:** `backend/src/Kernel/**`, `backend/src/Organizacao/**`, `backend/src/Api/**`, `backend/migrations/**`, `backend/tests/Architecture/**`
**Nature:** objective
**tdd-scope:** required
**Seams:** Contract (OpenAPI), Architecture test, RLS
**Status:** done-local

- [x] Rota autenticada sem contexto resolvido responde 401; sem vínculo com a unidade pedida responde 403 (`RequestContextEndpointsTests`)
- [x] Contexto alimenta `TenantDatabase`/`IUnitOfWork` sem o código de negócio montá-lo
- [x] Teste de arquitetura falha (`ClientContextInputTests`; o campo de destino da troca se chama `unidadeDestinoId`, que não é contexto) se DTO de entrada tiver campo `organizacaoId` ou `unidadeId` vindo do cliente
- [x] Troca de unidade é operação explícita, autorizada; usuário só vê unidades com vínculo (autorização = vínculo; chamada ao `DecidirAsync` e auditoria ficam para os tickets 06 e 08)
- [x] Dois tenants (`UsuarioUnidadeIsolationIntegrationTests`, `OrganizacaoVinculosIntegrationTests`): A não enxerga unidade nem vínculo de B (credencial da aplicação)
