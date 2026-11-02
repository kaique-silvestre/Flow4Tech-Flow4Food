# 07: Organizações com eventos pendentes para o dispatcher

**What to build:** Tabela-registro mantida na mesma transação do evento e função restrita de leitura pelo dispatcher, sem papel que ignore RLS. Atualiza ADR-003. Spec 5.8.
**Blocked by:** 02
**Touches:** `backend/src/Infrastructure/Outbox/**`, `backend/migrations/**`, `backend/tests/Outbox/**`, `docs/adr/003-outbox-e-fila.md`
**Nature:** objective
**tdd-scope:** required
**Seams:** RLS, Outbox
**Status:** pending

- [ ] Dispatcher entrega eventos de várias organizações sem ler dados de outra
- [ ] Falha entre gravar o evento e registrar a organização não deixa evento órfão (mesma transação)
- [ ] Nenhum papel com BYPASSRLS é criado
- [ ] Item aberto do ADR-003 fechado
