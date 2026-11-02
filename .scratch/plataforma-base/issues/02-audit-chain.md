# 02: Auditoria imutável encadeada e log técnico com correlação

**What to build:** Tabela de auditoria append-only por organização, hash encadeado, escrita na mesma transação da ação, verificação de cadeia, log técnico estruturado com id de correlação. Spec 5.6.
**Blocked by:** 01
**Touches:** `backend/src/Kernel/**`, `backend/src/Infrastructure/**`, `backend/migrations/**`, `backend/tests/**`
**Nature:** objective
**tdd-scope:** required
**Seams:** RLS, Outbox, Unit of work
**Status:** pending

- [ ] `UPDATE` e `DELETE` negados à credencial da aplicação por privilégio, com teste
- [ ] Adulterar, remover ou reordenar um registro é detectado pela verificação de cadeia
- [ ] Registro escrito na mesma `IUnitOfWork` da ação; ação [A] sem registro falha o teste
- [ ] Mesma id de correlação na requisição, no log técnico e na auditoria
- [ ] Varredura de log e auditoria não acha senha, token nem hash de senha
- [ ] Tabela passa pelo teste de arquitetura de RLS
