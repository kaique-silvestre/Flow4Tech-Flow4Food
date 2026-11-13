# F4-0058: Discount, courtesy and gift with limits decided by the engine

**What to build:** A permitted person gives a discount (percent or value, on an item or the comanda), a courtesy or a gift; the amount or percent is computed on the server from the stored comanda and the engine enforces per-profile limits, including the cumulative daily cap.
**Blocked by:** F4-0056, F4-0046
**Touches:** backend/src/Comandas/**, backend/src/Permissoes/**, backend/migrations/**, backend/src/Api/**, backend/tests/Authorization/**, backend/tests/**
**Nature:** objective
**tdd-scope:** required
**Seams:** Domain (pure); Real database (Testcontainers, app credential); Contract; Two tenants over HTTP (`DoisTenantsEndpointsTests` extended)
**Status:** done

- [x] Items get a type (`normal`, `cortesia`, `brinde`); courtesy/gift charge zero but keep their reason and original price for reports; distinct events `comanda.item-cortesia` and `comanda.item-brinde` carry what the future stock write-off needs.
- [x] Discount recorded as its own rows (item or comanda scope, percent or value), blocked from pushing the total below zero; calculation order fixed in ADR-018 (consumption, item discount, comanda discount, service fee on the discounted consumption, tip outside the balance).
- [x] Each action calls `DecidirAsync` inside the unit of work with `Value`/`Percent` taken from the server data; above the limit = 403 without reason; the cap reservation rolls back with the operation; denial audited (`sensitive`).
- [x] Permissions `comanda:desconto`, `comanda:cortesia`, `comanda:brinde` [A][L] in the catalog and in the authorization contract matrix; limits configurable per profile through the persisted rules.
- [x] Property tests: total never negative, rounding half-up in one pure function; concurrency test on the daily cap through HTTP.
- [x] ADR-018 written (calculation order and rounding).

## Decisões e desvios (F4-0058)
- A rota declara `comanda:ver`, não a permissão com limite: decisão de rota não leva valores e o motor nega permissão com limite sem valor. O caso de uso pergunta o limite com `Percent`/`Value` do servidor (mesmo padrão de `cardapio:preco-alterar`). Por isso a negação é auditada pelo caso de uso (`acesso:negar`), não pela rota `sensitive`.
- Cortesia e brinde valem a linha inteira (para dar parte, divida a quantidade antes); medem 100% e o valor de tabela.
- Desconto gravado não se desfaz neste ticket (só SELECT/INSERT); estorno de desconto fica para ticket próprio.
- Perfis modelo sem as três permissões (depende do dono definir os limites). `/sessao/permissoes` não lista permissão com limite: o F4-0059 precisa de uma pergunta "tem regra" para mostrar os botões.
- Taxa de serviço sobre o consumo descontado é a proposta Q4 do spec, ainda não confirmada pelo dono (ADR-018).
- Piso de mutação do Comandas: o filtro de testes do `stryker-comandas.json` ganhou os testes novos (67,0%, piso 64).

