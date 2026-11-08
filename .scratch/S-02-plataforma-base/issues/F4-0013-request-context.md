# F4-0013: Contexto de organização e unidade em toda requisição

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

## Riscos e pendências (revisão independente)
- Sessão sem unidade ativa responde 401 e `POST /sessao/unidade-ativa` exige sessão: o ticket 04 deve definir uma unidade padrão ou uma rota de seleção que funcione sem unidade ativa.
- Autorização da troca de unidade via `DecidirAsync` fica no ticket 06; auditoria da troca, no 08.
- A sessão de teste (`X-Test-Session`) fica ativa em `Testing` quando `Flow4Tech:TestSession:Key` está configurada: nunca configurar essa chave fora de testes.
- Gates acrescentados: `ClientContextInput` cobre tipos genéricos, campos públicos, nomes de binding explícitos (query/header/route/form) e os sinônimos `org`, `tenant`, `unidade`; `RoutePermissionTests` (grupo "session requirement") exige `.RequireSession()` em toda rota fora das allowlists (`/version` é pública de propósito).
