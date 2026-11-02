# 04: Sessões e tokens

**What to build:** Token de acesso curto com audiência, refresh rotativo com detecção de reuso, versão de sessão, revogação imediata, lista e encerramento de sessões, cookies seguros. Spec 5.3.
**Blocked by:** 03
**Touches:** `backend/src/Api/**`, módulo de identidade, `backend/migrations/**`, `docs/adr/**`
**Nature:** objective
**tdd-scope:** required
**Seams:** Contract (OpenAPI), RLS
**Status:** pending

- [ ] Reuso de refresh derruba a cadeia da sessão
- [ ] Desativar usuário, trocar senha e mudar permissão invalidam sessões na próxima requisição
- [ ] Token de um realm é recusado no outro
- [ ] Tempos de sessão configuráveis (valores padrão até o ticket 10)
- [ ] Chaves de assinatura vêm de variável de ambiente, rotacionáveis
- [ ] Latência da checagem de revogação medida e registrada
