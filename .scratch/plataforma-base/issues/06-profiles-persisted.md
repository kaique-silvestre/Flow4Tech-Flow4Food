# 06: Perfis e regras persistidos, anti-autopromoção e simulador

**What to build:** Perfis de fábrica e personalizados, modelos, overrides, escopo por unidade, limites e deny persistidos por organização, simulador pelo mesmo motor, matriz estendida. Spec 5.4.3 a 5.4.7, 5.4.9.
**Blocked by:** 05
**Touches:** `backend/src/Permissoes/**`, `backend/migrations/**`, `backend/tests/Authorization/**`
**Nature:** objective
**tdd-scope:** required
**Seams:** RLS, Authorization, Cumulative limits
**Status:** pending

- [ ] Matriz perfil x catálogo verde com as permissões novas, também sobre regras lidas do banco
- [ ] Perfis de fábrica não editáveis, só duplicáveis
- [ ] Mudar permissão, perfil ou vínculo invalida sessões afetadas
- [ ] Simulador devolve o mesmo resultado de `DecidirAsync`
- [ ] Teste de concorrência do teto continua verde com regras vindas do banco
