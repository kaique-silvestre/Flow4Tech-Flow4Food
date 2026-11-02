# 09: Convites, recuperação de acesso e login com Google

**What to build:** Convite de uso único com expiração, recuperação por e-mail, nova credencial temporária por quem tem permissão, OIDC Google com e-mail verificado e vínculo com confirmação. Spec 5.2.1, 5.2.3, 5.2.5, 5.2.6, 5.2.9.
**Blocked by:** 03, 04, 08
**Touches:** módulo de identidade, `backend/src/Api/**`, `backend/tests/**`
**Nature:** objective
**tdd-scope:** required
**Seams:** Contract (OpenAPI), Audit
**Status:** pending

- [ ] Token de convite e de recuperação guardados só como hash, uso único, com expiração
- [ ] Recuperação invalida todas as sessões e notifica o usuário
- [ ] Google só aceita e-mail verificado, nunca cria conta e exige confirmação para vincular
- [ ] Conta operacional não usa Google (ASSUMPTION, spec questão 6)
- [ ] Fluxo OIDC testado contra provedor falso, sem rede
