# 05: Permissões ligadas ao HTTP

**What to build:** `PermissionHandler` com `DecidirAsync`, `RequirePermission` passa a exigir autenticação, `x-permission` no `openapi.json`, 401 e 403 sem vazar motivo, log da razão. Spec 5.4.1, 5.4.2, 5.4.8.
**Blocked by:** 04
**Touches:** `backend/src/Api/**`, `backend/src/Permissoes/**`, `backend/tests/**`, `frontend/packages/api-client/**`
**Nature:** objective
**tdd-scope:** required
**Seams:** Contract (OpenAPI), Authorization
**Status:** pending

- [ ] Sem sessão 401; sem permissão 403; permitido 2xx, por rota de teste
- [ ] `openapi.json` expõe `x-permission` e o cliente é regenerado
- [ ] Negado em recurso sensível gera registro de auditoria
- [ ] Nenhum código ramifica por nome de perfil (teste de arquitetura)
