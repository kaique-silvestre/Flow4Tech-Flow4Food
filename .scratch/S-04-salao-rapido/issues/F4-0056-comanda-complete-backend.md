# F4-0056: Complete comanda I: edit items, rename, change waiter, people, history, cancel

**What to build:** Everything a waiter or manager changes on a comanda without touching money: item quantity/note, comanda name, responsible waiter, number of people, a readable history, and cancelling a comanda that has no payments.
**Blocked by:** F4-0044
**Touches:** backend/src/Comandas/**, backend/migrations/**, backend/src/Api/**, backend/tests/**
**Nature:** objective
**tdd-scope:** required
**Seams:** Domain (pure); Real database (Testcontainers, app credential); Contract; Two tenants over HTTP (`DoisTenantsEndpointsTests` extended)
**Status:** done

- [x] Alter item (quantity, note) with `comanda:alterar-item`; quantity down below received is refused as cancel already is; frozen price unchanged.
- [x] Rename, change waiter, number of people, general note: each with its permission, audited, `versao` checked.
- [x] History route: timeline of the comanda's events (who, what, when) read from the audit/outbox data without exposing secrets; permission `comanda:ver-historico`.
- [x] Cancel comanda: only with zero payments, with a standard reason; event `comanda.cancelada`; all open items become cancelled with the same reason.
- [x] A comanda in the middle of closing refuses new launches (lock flag set by the close attempt, released on failure); concurrency test.

## Decisões e desvios (F4-0056)
- Lock de fechamento: sem flag persistida; a corrida fecha/cancela/lança é decidida pela `versao` da mesma linha na mesma transação (ver `docs/agents/salao.md`). O critério "liberada na falha" é satisfeito por construção (falha = rollback). Teste de concorrência prova que nunca entra item em comanda fechada.
- "Número de pessoas e observação geral, cada um com sua permissão": o catálogo do spec não tinha permissão para eles; criada `comanda:alterar-dados` (uma só para os dois campos, que a tela envia juntos). Também nova: `comanda:ver-historico`.
- Perfis modelo (decisão do agente, a confirmar com o dono): Garçom `alterar-item|renomear|alterar-dados`; Gerente também `trocar-garcom|cancelar|ver-historico`. O seed e2e acompanha o Garçom.
- Histórico lê a trilha de auditoria (não o outbox, que é consumido e limpo); por isso inclui só ações que já são auditadas.
- `ComandaDto` ganhou `observacao` (campo novo, sem quebra para clientes que ignoram campos extras).

