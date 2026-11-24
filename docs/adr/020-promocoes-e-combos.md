# 020. Promoções e combos: regra persistida, avaliada no servidor, desconto de item automático

- Status: accepted
- Date: 2026-11-23

## Context
O gerente quer promoções e combos pontuais ou recorrentes, por horário e unidade, aplicados sozinhos e registrados no item, sem desconto na mão (spec S-05, G6). A conta tem uma ordem única de cálculo (ADR-018) e um arredondamento único; uma promoção nova não pode criar um segundo caminho que faça o total divergir ou ficar negativo, nem aceitar do cliente um número que decide desconto.

## Decision
- **Regra persistida** (`promocoes`, `promocao_produtos`, tabelas de tenant com RLS exata). Tipos: `percentual` (percentual sobre a linha), `valor` (valor fixo por unidade) e `combo` (produtos em quantidades fixas a um preço fixo por conjunto completo). `unidade_id` nulo vale para todas as unidades; senão só para aquela. Janela: ligada/desligada, validade absoluta (`valido_de` inclusive, `valido_ate` exclusivo), dias da semana e faixa de horário opcional no relógio local da unidade (fuso da agenda, `AgendaDaUnidade.HoraLocalAsync`); faixa que termina antes de começar atravessa a meia-noite e pertence ao dia em que começou. A regra se edita no lugar (`versao` contra edição concorrente, 409 `versao-desatualizada`), nunca se apaga (desliga-se) e o tipo e os produtos não mudam depois de criados (outro combo é outra regra).
- **O servidor decide, na hora do lançamento** (`GestaoDeRodadas`, relógio `IClock`): o cliente manda só produto, quantidade e opções; qualquer valor de desconto no corpo é ignorado. `RegrasDePromocao.Avaliar` (pura, em `Comandas.Domain`) devolve o que cada linha da rodada recebe.
- **Ordem da conta (ADR-018)**: a promoção entra como um desconto de item (`descontos_comanda` com `promocao_id`), dado na própria rodada, portanto ANTES de qualquer desconto manual. Passo 2 do ADR-018: promoção primeiro, desconto manual de item depois, desconto de comanda, taxa. O desconto manual enxerga como base o que sobrou da linha (`BaseDoItem`), então 50% manual sobre uma linha de 2000 com promoção de 200 vale 900, não 1000, e o total nunca é negativo (o corte é o mesmo `RegrasDeDesconto.Calcular`). O percentual usa o único arredondamento (`DoPercentual`).
- **Registrada no item**: `itens_comanda.promocao_id`, `promocao_nome` e `promocao_centavos` (o nome fica copiado porque a regra muda). `ItemDto` e `DescontoDto` mostram os três.
- **Quanto cada regra tira**: cada linha recebe NO MÁXIMO uma promoção. Combos primeiro, o de maior economia por conjunto primeiro (empate pelo id): precisa de todos os produtos na rodada, forma quantos conjuntos completos as unidades permitem, e a economia (valor de tabela dos conjuntos menos o preço do conjunto, nunca abaixo de zero) é dividida entre as linhas usadas em proporção ao preço do produto, a última leva o resto do arredondamento. Uma linha usada por combo não recebe outra promoção. As demais linhas recebem a melhor entre as regras de percentual e valor que listam o produto (empate pelo id). Opções (adicionais) não entram no preço que combo e valor fixo citam, mas o percentual incide sobre a linha inteira. Combo vale por rodada, não pela comanda.
- **Permissões**: `promocao:ver` e `promocao:gerenciar` (sem limite; aplicar uma promoção não exige permissão, é o servidor). O modelo Gerente recebe as duas (decisão do agente, NÃO confirmada pelo dono); Garçom e Caixa não.
- **Auditoria**: `promocao:criar` e `promocao:alterar` na mesma unidade de trabalho; a rodada que aplicou promoção registra `promocaoCentavos` na própria auditoria de `comanda:lancar-rodada`.

## Consequences
- Um só caminho muda a conta: promoção é desconto de item. Total, recebimento, divisão e comprovante já a tratam.
- Mudar quantidade de um item depois do lançamento não recalcula a promoção (mesma regra do desconto manual: valor gravado, cortado no cálculo); transferir item entre comandas não leva a promoção junto. Tickets próprios, se o dono pedir.
- Uma regra de 100% deixa a linha grátis: quem gerencia promoções responde por isso (auditoria `promocao:criar|alterar` e `promocao_id` na linha). Não há teto de percentual por perfil nesta fase.
- Regras sobrepostas não são recusadas: vale a melhor para o cliente; a tela (G6 front) pode avisar do conflito.

## Alternatives considered
- Guardar o desconto só no item e mudar `Calcular`/`BaseDoItem`: dois caminhos de desconto de item para manter em sincronia.
- Acumular várias promoções na mesma linha: o total passa a depender da ordem de avaliação e fica difícil de explicar ao dono.
- Combo avaliado sobre a comanda inteira: a rodada seguinte mudaria o desconto de itens já lançados.
