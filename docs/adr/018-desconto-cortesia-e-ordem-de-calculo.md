# 018. Desconto, cortesia, brinde e a ordem de cálculo da conta

- Status: accepted
- Date: 2026-11-12

## Context
A comanda passa a aceitar desconto (percentual ou valor, no item ou na comanda), cortesia e brinde, com limite por perfil decidido pelo motor de autorização (ADR-006), inclusive teto diário acumulado. Sem uma ordem de cálculo única e um arredondamento único, o total mostrado no celular, o saldo do caixa e o relatório podem divergir por centavos, e um desconto grande demais pode deixar o total negativo.

## Decision
**Ordem da conta** (uma função pura em `Comandas.Domain`, `RegrasDeDesconto.Calcular` e `RegrasDeComanda.Conta`):
1. **Consumo**: itens lançados, (preço congelado + opções) x quantidade. Cortesia e brinde entram com valor ZERO (a linha guarda o valor original em `ValorDeTabelaCentavos` para relatório e baixa de estoque futura).
2. **Desconto de item**: cada desconto de item sai da sua linha, na ordem em que foi dado, nunca abaixo de zero.
3. **Desconto de comanda**: sai do que sobrou do consumo, na ordem em que foi dado, nunca abaixo de zero.
4. **Taxa de serviço** sobre o consumo JÁ descontado (proposta Q4 do spec, ainda a confirmar com o dono), arredondada na função `TaxaDeServico`.
5. Couvert (quando existir) e gorjeta ficam FORA do saldo e entram depois, sem mudar os passos acima.
O total nunca é negativo: o corte acontece no cálculo, e o desconto novo é recusado (409 `desconto-excede`) se passar do que ainda é cobrável.

**Arredondamento**: um só lugar, `RegrasDeDesconto.DoPercentual` (meio centavo sobe, `MidpointRounding.AwayFromZero`), igual à taxa. Valores guardados em centavos inteiros; o percentual guardado tem duas casas.

**Quanto vale um desconto**: o servidor calcula os centavos na hora de dar (a partir do percentual ou do valor pedido e da comanda guardada) e GRAVA o resultado (`aplicado_centavos`); a conta usa o valor gravado, cortado no cálculo. Itens lançados depois não são alcançados por um desconto antigo, como o preço congelado no lançamento.

**Limites pelo motor**: `comanda:desconto|cortesia|brinde` são permissões com limite. A rota declara a permissão simples `comanda:ver` (decisão de rota não leva valores, e permissão com limite sem valor é negada) e o caso de uso pergunta ao motor DENTRO da unidade de trabalho e DEPOIS do bloqueio da linha da comanda (a troca de `versao`), com `Percent` e `Value` calculados do servidor: percentual pedido (ou, num desconto por valor, a fatia real do valor sobre a base ainda cobrável) e os centavos tirados; cortesia e brinde medem 100% e o valor de tabela da linha. Negado lança uma exceção que desfaz a troca de versão e o teto diário reservado; a resposta é 403 vazio e a negação é auditada em transação própria (`acesso:negar`, permissão, sem motivo).

## Consequences
- Um único lugar muda a conta; a tela, o recebimento, o fechamento e as checagens "não baixar do já recebido" usam a mesma função.
- O teto diário é reservado na mesma transação do desconto: sob concorrência passam exatamente os que cabem (teste com 8 descontos simultâneos).
- Desconto gravado não é editado nem apagado (a credencial da aplicação só tem SELECT e INSERT); corrigir um desconto dado por engano é um ticket próprio (estorno de desconto).
- `GET /sessao/permissoes` pergunta ao motor sem valores, então uma permissão com limite aparece como NÃO concedida; a tela de desconto (F4-0059) precisa de uma pergunta "tem alguma regra" para decidir o que mostrar.
- Se a taxa de serviço passar a incidir sobre o consumo bruto (Q4), muda só o passo 4.

## Alternatives considered
- Guardar só o percentual e recalcular a cada leitura: o total de uma comanda aberta mudaria sozinho quando entram itens, e o limite medido na hora de dar deixaria de valer.
- Taxa sobre o consumo bruto: o cliente pagaria serviço sobre o que ganhou de desconto; fica como alternativa se o dono decidir assim.
- Rota com a permissão limitada declarada: o motor nega por falta de valor; a decisão com valores precisa estar no caso de uso.
