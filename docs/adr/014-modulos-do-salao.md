# 014. Módulos do salão e suas dependências

- Status: accepted
- Date: 2026-11-05

## Context
A fatia `/salao` precisa de cardápio, comandas (com mesas e rodadas), pagamentos e caixa. A fronteira entre módulos é verificada por `backend/tests/Architecture` e `backend/modules.dependencies.json` (única fonte de verdade); é preciso decidir quantos módulos, onde cada conceito mora e quais arestas existem, sem ciclos.

## Decision
Quatro módulos novos, cada um com Domain/Application/Infrastructure/Interface: `Cardapio`, `Comandas`, `Pagamentos`, `Caixa`. Arestas permitidas, e apenas estas: `Comandas -> Cardapio`, `Comandas -> Pagamentos`, `Pagamentos -> Caixa`. `Cardapio` e `Caixa` não dependem de nenhum módulo de negócio. Ninguém depende de `Comandas`: quem precisar reagir a ela usa eventos (outbox, ADR-003).

- Mesa mora em `Comandas`. Mesa só existe para agrupar comandas abertas e rodadas; não tem catálogo, preço nem ciclo de vida próprios fora delas. Um módulo `Mesas` separado criaria uma aresta `Comandas -> Mesas` para guardar um rótulo e um estado derivado das comandas, e uma regra (mesa livre/ocupada) que só `Comandas` sabe calcular.
- `Pagamentos -> Caixa`. Todo pagamento em dinheiro entra no turno de caixa aberto do operador (troco incluído) e só pode ser registrado se houver turno aberto. `Pagamentos` chama o contrato público de `Caixa.Application`; `Caixa` nunca conhece pagamentos (recebe o lançamento, não o motivo). A direção inversa criaria ciclo.
- Cada módulo declara a aresta com `ProjectReference` apenas para o projeto Application do alvo; o Api referencia o Application de cada módulo para que os catálogos de permissão sejam coletados.

## Consequences
O grafo de módulos continua acíclico e o teste de arquitetura segue sendo o portão. Fechar uma comanda coordena Pagamentos (e por ele Caixa) pelo contrato, na mesma unidade de trabalho. Dividir Pagamentos ou Caixa depois, ou mover mesa para fora de Comandas, exige nova aresta e novo ADR.

## Alternatives considered
- Um único módulo `Salao`: menos arquivos, mas apaga as fronteiras que o teste de arquitetura protege e mistura caixa com cardápio.
- Mesa como módulo próprio: aresta e contrato a mais para um conceito sem regras próprias.
- `Caixa -> Pagamentos` (caixa consulta pagamentos): faz o caixa depender de quem o alimenta; inverte a direção natural e acopla o turno ao meio de pagamento.
