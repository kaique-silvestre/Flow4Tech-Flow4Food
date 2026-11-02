# 08: Proteção de login e alertas de segurança

**What to build:** Bloqueio progressivo por conta e por IP, detecção de credential stuffing, interface de CAPTCHA, alerta de login novo, eventos de segurança no outbox, interface de e-mail com implementação de desenvolvimento. Spec 5.5.
**Blocked by:** 04, 07
**Touches:** módulo de identidade, `backend/src/Infrastructure/**`, `backend/tests/**`
**Nature:** objective
**tdd-scope:** required
**Seams:** Outbox, Audit
**Status:** pending

- [ ] Bloqueio progressivo por conta e por IP demonstrado em teste
- [ ] Resposta de login e recuperação sem enumeração
- [ ] Login em local ou aparelho novo gera evento e alerta ao usuário e ao dono
- [ ] Reuso de refresh, bloqueio e troca de senha geram evento de segurança
