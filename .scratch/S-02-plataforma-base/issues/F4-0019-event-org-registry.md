# F4-0019: Organizações com eventos pendentes para o dispatcher

**What to build:** Tabela-registro mantida na mesma transação do evento e função restrita de leitura pelo dispatcher, sem papel que ignore RLS. Atualiza ADR-003. Spec 5.8.
**Blocked by:** F4-0014
**Touches:** `backend/src/Infrastructure/Outbox/**`, `backend/migrations/**`, `backend/tests/Outbox/**`, `docs/adr/003-outbox-e-fila.md`
**Nature:** objective
**tdd-scope:** required
**Seams:** RLS, Outbox
**Status:** done-local (código e testes escritos; testes com PostgreSQL NÃO rodados, ficam para o Mac; sem revisão independente)

- [~] Dispatcher entrega eventos de várias organizações sem ler dados de outra. `OutboxSweeper`/`PendingOrganizationsDatabase` + `OutboxPendingRegistryIntegrationTests.The_sweep_delivers_events_of_several_organizations_each_under_its_own_context_and_clears_the_registry` (consumidor conta `amostra_itens` visível = 1). Teste escrito, NÃO rodado (Testcontainers). As funções SQL foram exercitadas à mão num PostgreSQL 16 local (registro, rollback, fechamento, privilégio negado, recusa em contexto)
- [~] Falha entre gravar o evento e registrar a organização não deixa evento órfão (mesma transação). Gatilho `AFTER INSERT` em `outbox_eventos` (migração 0011): o registro nasce no mesmo statement/transação do evento; testes `A_failure_after_the_event_is_written_leaves_neither...`, `A_rejected_event_insert_rolls_back...`, `Closing_does_not_remove_an_organization_whose_event_is_still_uncommitted...` (corrida com o fechamento via trava de linha). Escritos, NÃO rodados; rollback verificado à mão no PG 16 local
- [x] Nenhum papel com BYPASSRLS é criado. Migração só cria tabela, 3 funções (SECURITY DEFINER, `search_path` fixo, PUBLIC revogado) e gatilho; backfill alterna `NO FORCE`/`FORCE` na própria transação. Testes `No_role_that_bypasses_row_level_security_was_created` (Outbox) e o existente em Identidade (não rodados); por leitura da migração, nenhum `CREATE ROLE`/`BYPASSRLS`
- [x] Item aberto do ADR-003 fechado. `docs/adr/003-outbox-e-fila.md`: F2 fechado, seção "Organization discovery (built)"; `CLAUDE.md` atualizado. Build limpo, `dotnet format` sem achados novos (só CA1515 conhecido), Architecture 125/125 verde
