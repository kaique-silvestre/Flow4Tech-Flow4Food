# F4-0017: Permissões ligadas ao HTTP

**What to build:** `PermissionHandler` com `DecidirAsync`, `RequirePermission` passa a exigir autenticação, `x-permission` no `openapi.json`, 401 e 403 sem vazar motivo, log da razão. Spec 5.4.1, 5.4.2, 5.4.8.
**Blocked by:** F4-0016
**Touches:** `backend/src/Api/**`, `backend/src/Permissoes/**`, `backend/tests/**`, `frontend/packages/api-client/**`
**Nature:** objective
**tdd-scope:** required
**Seams:** Contract (OpenAPI), Authorization
**Status:** done-local (suíte verde; cliente TypeScript não regenerado neste ambiente; sem revisão independente)

- [x] Sem sessão 401; sem permissão 403; permitido 2xx (`PermissoesEndpointsTests`: `Without_a_session...`, `A_permission_the_profile_has_is_allowed`, `A_permission_the_profile_lacks_answers_403...`, `An_account_without_any_profile...`; usa rotas reais com perfis distintos, não rotas de teste dedicadas)
- [ ] `openapi.json` expõe `x-permission` (feito, `RoutePermissionTests.The_committed_openapi_document_exposes_x_permission...`) e o cliente é regenerado (**pendente**: sem node/pnpm aqui; o gate `openapi-contract` do CI acusa se mudar)
- [x] Negado em recurso sensível gera registro de auditoria (`acesso:negar`, opt-in por `sensitive: true`; `A_denial_on_a_sensitive_route_is_audited...`)
- [x] Nenhum código ramifica por nome de perfil (`ProfileBranchingTests`)

## Riscos e pendências
- Perfis ainda são provisórios: `PerfisDaConta` dá `dono` ao dono e `conta` aos demais (ticket 06 persiste perfis, overrides e escopo por unidade).
- `sessao:revogar` (de terceiros) e `sessao:ver-todas` ainda têm o teste "ser dono" dentro de `Sessoes`; a rota já exige a permissão no motor. Falta o serviço perguntar ao motor e separar de vez.
- Rotas decididas aqui não têm valores da operação: permissão com limite continua sendo pedida no handler de aplicação.
- Auditoria de negação é opt-in (só `GET /sessoes/todas` está marcada); o alerta de acesso negado em série é do ticket 08. Auditar negação toma o lock da cadeia da organização.
- Cliente TypeScript (`frontend/packages/api-client`) não foi regenerado.
