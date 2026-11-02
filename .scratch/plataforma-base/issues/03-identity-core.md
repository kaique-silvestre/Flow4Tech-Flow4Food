# 03: Identidade: contas, senhas e pré-autenticação sob RLS

**What to build:** Contas com e-mail e contas operacionais, Argon2id, checagem de senha vazada, credencial temporária com troca obrigatória, função SECURITY DEFINER mínima para o lookup de login, ADR-005. Spec 5.2.1, 5.2.2, 5.2.4, 5.2.7, 5.2.8.
**Blocked by:** 01, 02
**Touches:** `backend/src/Organizacao/**` (ou novo módulo `Identidade`), `backend/migrations/**`, `docs/adr/**`, `backend/modules.dependencies.json`
**Nature:** objective
**tdd-scope:** required
**Seams:** RLS, Architecture test
**Status:** pending

- [ ] Lookup de login não usa papel que ignora RLS; exceção listada em `RlsArchitectureIntegrationTests`
- [ ] Login não revela se usuário, organização ou e-mail existem; tempo uniforme
- [ ] Credencial temporária mostrada uma única vez; sessão inicial só troca a senha
- [ ] Anti-autopromoção, sempre um dono e dono não rebaixado têm teste
- [ ] ADR-005 escrito e aceito
- [ ] ASSUMPTION: uma conta pertence a uma só organização (spec, questão 1); revisar se o dono disser o contrário
