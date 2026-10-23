# Novo sistema — Catálogo de features

Status: rascunho 1. Cobre Comandas, Cardápio e Estoque. Ordem de detalhamento conforme definido em conversa.
Convenção de permissões: `recurso:ação`. Ações marcadas com **[L]** aceitam limite/condição (valor, %, horário). Ações marcadas com **[A]** são auditadas com motivo obrigatório.
Escopo de toda permissão: organização ou unidade(s) específicas (ver D3, D10).

---

## F1. Comandas

Conta aberta em uma unidade, onde itens são lançados até o fechamento. Suporta vários clientes na mesma comanda.

### Conceitos
- **Comanda**: conta aberta. Identificada por número/nome e opcionalmente mesa.
- **Mesa** (opcional): local físico. Uma mesa pode ter várias comandas (cada cliente com a sua) ou uma comanda compartilhada.
- **Cliente/assento** (opcional): subdivisão dentro da comanda, para dividir a conta por pessoa.
- **Item lançado**: produto do cardápio + quantidade + opções (adicionais, observações) + preço congelado no momento do lançamento.
- **Estados da comanda**: `aberta` → `fechada` | `cancelada`; `fechada` → `reaberta` (exceção controlada).
- **Estados do item**: `lançado` → `preparando`/`entregue` (se houver cozinha) | `cancelado` | `cortesia`/`brinde`.

### Capacidades
| # | Capacidade | Detalhe |
|---|---|---|
| F1.1 | Abrir comanda | Informar identificação, mesa, nº de pessoas, responsável (garçom). |
| F1.2 | Lançar itens | Quantidade, adicionais, observação, vários itens de uma vez. |
| F1.3 | Alterar item | Quantidade, observação. Após envio à cozinha, só por permissão. |
| F1.4 | Cancelar item | Motivo obrigatório. Estorna estoque se já baixado. |
| F1.5 | Cancelar comanda | Motivo obrigatório. Só se sem pagamentos, ou estornando-os. |
| F1.6 | Cortesia | Item cobrado a custo zero para o cliente, **com consumo de estoque**. Motivo obrigatório. |
| F1.7 | Brinde | Item dado como ação comercial (ex.: promoção, "compre X leve Y"), com consumo de estoque. Distinto de cortesia para relatório. |
| F1.8 | Desconto | Percentual ou valor, em item ou na comanda. Limite por perfil. |
| F1.9 | Taxa de serviço / couvert | Configurável por unidade; remover exige permissão. |
| F1.10 | Transferir | Item entre comandas; comanda entre mesas; juntar/separar comandas. |
| F1.11 | Dividir conta | Por pessoa, por item, em partes iguais. |
| F1.12 | Pagamento | Vários métodos, parcial e dividido; troco; gorjeta. |
| F1.13 | Fechar comanda | Exige saldo zerado. Gera comprovante. |
| F1.14 | Reabrir comanda | Exceção auditada; reverte efeitos do fechamento. |
| F1.16 | Mapa de mesas | Visão do salão por área, **derivada das comandas abertas** (ver abaixo). |
| F1.15 | Impressão/visualização | Pré-conta, comprovante, envio de itens à cozinha/bar. |

### Permissões derivadas
```
comanda:ver                 comanda:criar
comanda:lancar_item         comanda:alterar_item
comanda:cancelar_item [A]   comanda:cancelar [A]
comanda:cortesia [A][L]     comanda:brinde [A][L]
comanda:desconto [A][L]     comanda:remover_taxa_servico [A]
comanda:transferir_item     comanda:transferir_mesa
comanda:juntar              comanda:dividir
comanda:receber_pagamento   comanda:estornar_pagamento [A]
comanda:fechar              comanda:reabrir [A]
comanda:ver_comandas_de_outros   (sem isso, garçom só vê as próprias)
```
Limites típicos [L]: `desconto_max_percentual`, `cortesia_max_valor` por comanda/dia, `cancelar_item_apos_envio`.

### Lançamento rápido pelo app do garçom (canal principal)
Requisito vindo do cliente: em horário de pico não há tempo de "marcar na comanda". O **celular do garçom é o canal principal de lançamento**; a web é a central de visualização e gestão (comandas ao vivo, edição quando preciso).
- **Onde fica:** uma **rota própria, mobile-first e simplificada** do mesmo site (ex.: `/salao`), instalável na tela inicial (PWA). Mostra só o que o garçom precisa (abrir, lançar, enviar); o resto do sistema fica na versão web de gestão. Mesma API e mesmas permissões.
- **Meta de uso:** lançar um item comum em **até 2 toques**; abrir comanda e lançar o primeiro item em **menos de 10 segundos**.
- **Recursos de velocidade:** mais vendidos e favoritos do garçom no topo; **repetir a última rodada**; busca por nome, código ou leitura de código de barras; quantidade com teclado numérico grande; adicionais mais usados do item em um toque; atalho "mesma comanda de antes"; ditado de voz para a observação.
- **Fluxo de rodada:** juntar vários itens e **enviar de uma vez**, com confirmação visível.
- **Resiliência de rede:** fila de ações pendentes **visível** (o que foi enviado, o que está esperando), reenvio automático e sem perder lançamento em queda curta de sinal. Operação offline completa continua fora de escopo (D7).
- **Central na web:** lista e mapa de comandas em tempo real, filtros por garçom, mesa e tempo aberto, edição de comanda com permissão. Mudança feita em qualquer dispositivo aparece nos outros (SSE, versão da comanda).
- **Opcional futuro:** pedido pelo próprio cliente via QR (reduz o trabalho do garçom), já descrito em `02e` G13.

### Mesas e mapa de mesas (como se integra à comanda)
- **Mesa é um cadastro opcional** (área, número, capacidade, posição no mapa), por unidade. A unidade pode **ligar ou desligar o uso de mesas**: um bar que trabalha só com nome ou cartão de comanda não precisa de nenhuma.
- **A comanda pode apontar para uma mesa** (a comanda continua existindo sem mesa). Uma mesa pode ter **várias comandas** (cada cliente com a sua) e uma comanda pode mudar de mesa.
- **O status da mesa não é guardado, é calculado a partir das comandas:**
  - **livre** = sem comanda aberta;
  - **ocupada** = com uma ou mais comandas abertas;
  - **aguardando conta** = comanda com pré-conta emitida ou conta pedida;
  - **reservada** = reserva ativa (quando existir reservas);
  - **tempo de ocupação** = tempo desde a abertura da primeira comanda da mesa.
- O **mapa é apenas uma visão** sobre as comandas: tocar numa mesa abre a comanda dela (ou cria uma, se estiver livre). Nada fica duplicado nem pode divergir.
- Permissões: `mesa:ver`, `mesa:gerenciar [A]` (criar, mover no mapa, desativar), `comanda:transferir_mesa` (já existia).
- **Regra de modelo (sem criar feature):** a comanda **não exige mesa**; tem um campo de tipo/canal reservado, com valor padrão "salão". Balcão e retirada hoje são comandas com nome. Delivery, se vier, entra por esse campo sem refazer o modelo.

### Decisões fechadas
- Item congela preço e ficha técnica no lançamento (preço e custo históricos não mudam depois).
- **Baixa de estoque no lançamento** (ver F3.5). Cancelar item estorna.
- **Cozinha/bar (KDS e impressão por praça) fica no escopo do produto**: item tem ciclo de estados e destino (praça). A divisão entre o que sai na primeira entrega e o que vem depois é feita no roadmap (ver `06-roadmap`, a criar).

### Capacidades adicionais (completude)
Do sistema atual e de pedidos de operação, também entram:
- **Identificação por nome ou por mesa**, e **numeração sequencial diária** de comandas (zera a cada dia operacional).
- **Motivos padronizados de cancelamento** (cliente desistiu, erro de lançamento, item indisponível, outro), configuráveis.
- **Tempo de comanda aberta em tempo real** (cronômetro e alerta por limite).
- **Atalho do garçom ao abrir comanda** (o garçom logado já vem preenchido; troca com permissão).
- **Número de pessoas** e **pessoa associada ao item** (base da divisão por assento).
- **Promoção aplicada ao item** registrada (qual promoção, valor do desconto).
- **Lista de comandas abertas e fechadas** com busca e filtros; contador de abertas.
- **Versão da comanda** (controle de concorrência otimista) para evitar sobrescrita entre dispositivos.

Além da tabela acima, uma comanda precisa de:
- Renomear/identificar comanda, trocar garçom responsável, alterar nº de pessoas.
- Observação geral da comanda e por item.
- Reordenar/agrupar itens por cliente/assento; mover item entre assentos.
- Reimprimir pré-conta e comprovante; segunda via.
- Lançamento rápido (favoritos, busca, por código).
- Bloquear lançamento em comanda em fechamento (evita concorrência).
- Histórico completo de eventos da comanda (linha do tempo auditável).
- Concorrência: dois dispositivos lançando na mesma comanda sem perder itens.
- Comandas abertas há muito tempo (alerta/fechamento forçado por permissão).

```
comanda:renomear   comanda:trocar_garcom   comanda:editar_observacao
comanda:ver_historico   comanda:reimprimir   comanda:fechar_forcado [A]
```

---

## F2. Cardápio

### Conceitos
- **Produto**: item vendável. Nome, categoria, preço, ativo/inativo, disponibilidade (por unidade, horário), foto, descrição.
- **Categoria**: hierárquica.
- **Opções do produto**: grupos de adicionais/modificadores (ex.: ponto da carne, extras com preço, remoções sem custo).
- **Promoção**: regra de preço aplicada automaticamente (por produto/categoria, janela de tempo, dia da semana, "leve X pague Y").
- **Combo**: produto composto por outros. Dois tipos:
  - **Fixo**: lista fechada de produtos (ex.: burger + fritas + refri).
  - **Com escolhas**: grupos onde o cliente escolhe (ex.: "escolha 1 bebida entre A, B, C").
- **Ficha técnica**: composição do produto em insumos e quantidades. **Opcional**: produto sem ficha vende normalmente, mas sem baixa de estoque nem CMV.

### Sobre ficha técnica (prática do mercado)
A ficha técnica (recipe/BOM) é padrão em sistemas de restaurante e serve para: baixa automática de estoque por venda, cálculo de custo e CMV, e precificação. Pontos comuns que o plano deve prever:
- **Unidades e conversão**: compra em caixa/kg, consumo em g/ml/un.
- **Fator de correção e rendimento**: perda no preparo (ex.: 1 kg de carne rende 800 g úteis).
- **Sub-receitas**: preparo intermediário (molho, massa) usado em vários produtos.
- **Ficha de combo**: custo do combo = soma das fichas de seus componentes.
- **Ficha congelada** nas vendas passadas: alterar a ficha hoje não reescreve o custo histórico.

### Capacidades
| # | Capacidade | Detalhe |
|---|---|---|
| F2.1 | CRUD de produtos e categorias | Ativar/desativar sem apagar. |
| F2.2 | Preço por unidade | Organização define base; unidade pode sobrescrever. |
| F2.3 | Opções e adicionais | Com preço, limites min/max, obrigatórios. |
| F2.4 | Promoções | Janela, recorrência, prioridade e regra de empilhamento. |
| F2.5 | Combos | Fixos e com escolhas; preço próprio ou soma com desconto. |
| F2.6 | Ficha técnica | Insumos, quantidades, rendimento, sub-receitas. |
| F2.7 | Custo e margem | CMV por produto, margem, alerta de margem baixa. |
| F2.8 | Disponibilidade | "Acabou" manual, ou automática quando insumo da ficha zera. |
| F2.9 | Produção possível | Por produto com ficha, quantas unidades ainda dá para produzir com o estoque disponível (mínimo entre os insumos); "sem ficha" quando não houver. Aparece na listagem do cardápio e no lançamento. |
| F2.10 | Categorias e subcategorias | Hierarquia de pelo menos dois níveis, filtro por subcategoria, ativar/desativar. |
| F2.11 | Preço promocional | Preço de venda e promocional que se sobrepõe ao normal enquanto vigente. |
| F2.12 | Promoções com tipo de desconto | Percentual ou valor fixo, vinculada a produtos, janela de datas e horas, recorrência por dias da semana ou do mês, ativação, visão mensal. |

### Permissões derivadas
```
cardapio:ver                cardapio:produto_criar
cardapio:produto_editar     cardapio:produto_desativar
cardapio:preco_alterar [A][L]   (limite de variação %)
cardapio:promocao_gerenciar     cardapio:combo_gerenciar
cardapio:ficha_ver              cardapio:ficha_editar [A]
cardapio:custo_ver              (dado sensível: custo e margem)
cardapio:marcar_indisponivel
```

---

## F3. Estoque

### Conceitos
- **Insumo**: item de inventário (ingrediente ou material). Unidade base, categoria, estoque mínimo, custo.
- **Cadastro de insumo → estoque**: cadastrar o insumo o torna controlável em estoque (como você descreveu). Insumo pode existir sem controle de estoque ativo (ex.: itens de baixo valor).
- **Local de estoque** (opcional): depósito, câmara, bar. Estoque é por unidade e, opcionalmente, por local.
- **Movimentação**: registro imutável de entrada/saída. O saldo é derivado das movimentações.
- **Fornecedor**: cadastro, contato, condições, insumos fornecidos.
- **Compra**: pedido ao fornecedor → recebimento → entrada no estoque, gerando custo e conta a pagar.
- **Custo médio ponderado**: recalculado a cada entrada.

### Tipos de movimentação
`entrada_compra`, `saida_venda` (por ficha técnica), `estorno_venda`, `estorno_compra`, `ajuste_inventario` (contagem), `perda` (quebra/vencimento/desperdício), `cortesia`, `consumo_interno`, `estorno_consumo_interno`, `transferencia` (entre unidades/locais).
**Motivos de baixa sem venda:** perda, quebra, cortesia e outro (configuráveis).

### Consumo interno (simplificado)
- Registrado na tela de estoque como baixa com motivo `consumo_interno`; não tem módulo, menu nem tela própria.
- Dados: item (insumo ou produto com ficha), quantidade, **consumidor opcional** (colaborador), observação, custo congelado.
- Visível no extrato de movimentações; relatórios de estoque e CMV mostram **consumo interno** como categoria própria (separada de vendas e de perdas).
- Estorno: novo movimento de reversão com motivo, nunca exclusão.
- Permissões: `estoque:consumo_interno [A][L]` (lançar, com limite de valor por dia opcional) e `estoque:consumo_interno_estornar [A]`.

### Capacidades
| # | Capacidade | Detalhe |
|---|---|---|
| F3.1 | Cadastro de insumos | Unidade, conversões, mínimo, ativar controle de estoque. |
| F3.2 | Fornecedores | CRUD, histórico de compras e preços. |
| F3.3 | Compras | Criar, receber (total/parcial), cancelar. Importação de NF-e XML. |
| F3.4 | Entrada de estoque | Via compra ou manual com motivo. |
| F3.5 | Baixa por venda | Automática via ficha técnica. Momento da baixa: ver pontos a decidir. |
| F3.6 | Inventário / contagem | Contar, comparar com saldo, gerar ajuste auditado. |
| F3.7 | Perdas e consumo interno | Motivo obrigatório, entra no CMV/relatórios. |
| F3.8 | Alertas | Abaixo do mínimo, sem movimentação, vencimento (se houver lote). |
| F3.9 | Histórico e rastreio | Extrato por insumo; quem moveu, quando, por quê. |
| F3.10 | Transferência | Entre unidades/locais da organização. |
| F3.11 | Consumo interno (dentro do estoque) | **Sem módulo nem tela própria.** É um **motivo de baixa do estoque**: na tela de estoque, a ação "Registrar consumo interno" baixa o insumo ou o produto (pela ficha), com consumidor opcional, observação e custo congelado, e aparece no extrato de movimentações e nos relatórios de estoque e CMV como categoria separada de perdas. Estorno por movimento de reversão. |
| F3.12 | Reserva e saldo disponível | Estoque atual, reservado e disponível por insumo; reserva no lançamento e conclusão no fechamento, sem condição de corrida entre comandas simultâneas. |
| F3.13 | Código de barras e embalagem | EAN por insumo, quantidade por caixa/embalagem, unidade base (unidade, grama, quilo, litro, mililitro) com conversões. |
| F3.14 | Estorno de compra | Reverter uma compra recebida ajusta estoque e custo médio e cancela a conta a pagar vinculada. |
| F3.15 | Compras por tipo | À vista (imediata), agendada (com data e hora previstas de recebimento) e a prazo (com data prevista de pagamento); confirmar recebimento, editar, cancelar. |
| F3.16 | Importação de NF-e com conciliação | Ler o XML, identificar fornecedor por CNPJ, casar itens com insumos por EAN, código e descrição, mostrar divergências para conferência antes de confirmar, gerar a compra e a conta a pagar. Validação segura do arquivo (tamanho, tipo real, proteção contra XML malicioso). |

### Permissões derivadas
```
estoque:ver                  estoque:ver_custo
insumo:criar                 insumo:editar          insumo:desativar
estoque:entrada_manual [A]   estoque:ajustar [A][L]
estoque:registrar_perda [A]  estoque:inventario_contar
estoque:inventario_aprovar [A]   estoque:transferir
fornecedor:ver               fornecedor:gerenciar
compra:criar                 compra:receber         compra:cancelar [A]
compra:importar_nfe          compra:ver_precos
```

### Decisões fechadas
- **Baixa no lançamento do item.** Estorno ao cancelar/alterar.
- **Estoque negativo** configurável por **organização** e por **unidade** (a unidade herda da organização e pode sobrescrever se permitido). Se bloqueado, lançar item sem saldo é recusado; se permitido, vende e gera alerta.
- **Limite de alerta configurável por insumo** (ex.: avisar com 5 unidades, ou com 20% do mínimo), com canais configuráveis.

### Alertas de estoque: como manter sempre atualizado
Polling constante é a pior opção (carga inútil, atraso). Melhor prática, em camadas:
1. **Orientado a evento (principal).** Toda movimentação de estoque, ao ser gravada, reavalia o limite do insumo naquela transação. Cruzou o limite, gera o alerta na hora. Custo zero quando nada acontece.
2. **Entrega em tempo real.** O alerta chega ao navegador por canal push (SSE ou WebSocket) e por notificação in-app. Sem polling no cliente.
3. **Rotina periódica leve (rede de segurança).** Job agendado a cada poucos minutos reconcilia saldos e dispara alertas que dependem do tempo (validade, "sem movimentação", itens que cruzaram o limite por ajuste em lote).
4. **Antirruído.** Alerta dispara na **transição** (acima→abaixo do limite), não a cada venda. Reaparece só após reposição e nova queda. Agrupamento e silenciar por insumo.
5. **Fonte da verdade.** Saldo derivado de movimentações; o valor exibido vem de um saldo materializado atualizado na mesma transação, para leitura rápida e consistente.

Canais previstos: in-app (sempre), e-mail e WhatsApp (depois), por perfil/usuário inscrito no alerta.

```
estoque:alerta_configurar   estoque:alerta_receber   estoque:politica_negativo_configurar [A]
```

### Pontos ainda em aberto
- Lotes e validade: v1 ou depois? (Hoje assumido: depois, modelo preparado.)
- Multi-local por unidade na v1? (Hoje assumido: modelo preparado, UI simples primeiro.)

---

## F4. Pagamentos

### Conceitos
- **Método de pagamento**: cadastrável e editável por organização, com **padrões de fábrica** (dinheiro, crédito, débito, Pix, vale-refeição/alimentação, fiado/conta de cliente, voucher, cheque) que podem ser desativados, renomeados ou ampliados com novos.
- **Pagamento**: valor recebido, método, comanda, assento (opcional), operador, caixa. Imutável; correção é feita por estorno.
- Uma comanda tem vários pagamentos ao longo do tempo, de métodos distintos, e pode fechar só quando o saldo chega a zero.

### Particularidades por método (atributos configuráveis)
| Atributo | Exemplos |
|---|---|
| Troco | só dinheiro |
| Parcelamento | crédito, com regras por parcela |
| Taxa/desconto da operadora | MDR crédito/débito, taxa de vale-refeição, por bandeira |
| Prazo de recebimento | D+0 Pix, D+1 débito, D+30 crédito (alimenta contas a receber) |
| Confirmação | síncrona (dinheiro) ou **assíncrona** (Pix, TEF) com estado `pendente → confirmado/falhou` |
| Identificação do cliente | obrigatória no fiado |
| Limite | fiado com limite por cliente; voucher com saldo e validade |
| Permite gorjeta, permite desconto, permite estorno | por método |

### Formas de dividir a conta
1. **Igualmente** entre N pessoas (resto de centavos tratado de forma determinística).
2. **Por item/assento**: cada pessoa paga o que consumiu.
3. **Valores diferentes** definidos livremente (ex.: R$ 50 + R$ 30 + resto).
4. **Por percentual**.
5. **Parcial** sem dividir: paga uma parte agora e o resto depois (comanda segue aberta).
6. **Misto**: um mesmo "pagador" quita com mais de um método.
Itens de rateio comum (taxa de serviço, couvert) seguem a regra escolhida e entram na divisão.

### Capacidades
| # | Capacidade | Detalhe |
|---|---|---|
| F4.1 | Cadastro de métodos | Padrões + customizados, atributos acima. |
| F4.2 | Registrar pagamento | Total, parcial, dividido, misto. Troco e gorjeta. |
| F4.3 | Divisão da conta | As seis formas acima, com pré-visualização. |
| F4.4 | Pagamento assíncrono | Pix/TEF com estado pendente e conciliação. |
| F4.5 | Estorno | Total ou parcial, motivo obrigatório, volta ao caixa correto. |
| F4.6 | Fiado / conta de cliente | Cliente, limite, extrato, recebimento posterior. |
| F4.7 | Vouchers | Emissão, saldo, validade, uso. |
| F4.8 | Conciliação | Pagamentos esperados vs recebidos por operadora/adquirente. |

### Preparado (não construído agora): TEF e Pix automático
Pagamento tem uma interface de **provedor** (adapter): `iniciar → aguardar confirmação → confirmar/falhar/cancelar`. Hoje o único provedor é o "manual" (operador registra). TEF e Pix automático entram como novos provedores sem mudar comanda nem caixa. Webhooks de confirmação passam por idempotência.

### Permissões derivadas
```
pagamento:ver               pagamento:registrar
pagamento:dividir           pagamento:registrar_gorjeta
pagamento:estornar [A][L]   pagamento:aceitar_fiado [A][L]
metodo_pagamento:gerenciar [A]    metodo_pagamento:ver_taxas
voucher:emitir [A][L]       voucher:usar
conciliacao:ver             conciliacao:ajustar [A]
```

---

## F5. Caixa

### Conceitos
- **Caixa**: ponto de recebimento da unidade (pode haver vários). Operado por um usuário durante um **turno**.
- **Turno/sessão de caixa**: abertura (com fundo de troco) → movimentos → fechamento com conferência.
- **Janela operacional da unidade**: período em que a unidade opera (24h, 12h, 8h, etc.), com **dia operacional** que pode atravessar a meia-noite.

### Configurável por organização e por unidade
- Duração e horários da janela operacional; fuso horário.
- **Quando é permitido registrar**: horários específicos por perfil/usuário; fora da janela, bloquear lançamento, pagamento ou ambos.
- Exigir caixa aberto para receber pagamento (sim/não).
- Um caixa por operador ou caixa compartilhado.
- Conferência cega no fechamento (operador não vê o valor esperado).
- Tolerância de diferença e quem aprova divergências.
- Fechamento automático ao fim da janela, ou lembrete.

### Capacidades
| # | Capacidade | Detalhe |
|---|---|---|
| F5.1 | Abrir caixa | Operador, fundo de troco. |
| F5.2 | Suprimento | Entrada de dinheiro no caixa, com motivo. |
| F5.3 | Sangria | Retirada de dinheiro, com motivo e destino. |
| F5.4 | Fechar caixa | Contagem por método, diferença (sobra/falta), aprovação. |
| F5.5 | Fechamento do dia operacional | Consolida todos os caixas da unidade. |
| F5.6 | Histórico | Turnos, movimentos, divergências, por operador. |
| F5.7 | Reabertura | Exceção auditada. |
| F5.8 | Regras de horário | Janela e restrições de registro configuráveis. |

### Como funciona a conferência e a aprovação de diferença
1. No fechamento, o operador **conta o dinheiro** (e informa os totais dos demais métodos). O sistema calcula o **esperado**: abertura + recebimentos em dinheiro + suprimentos − sangrias − troco. A **diferença** (sobra ou falta) é a conta.
2. Se a diferença está **dentro da tolerância** configurada (ex.: até R$ 5), o fechamento conclui sozinho e a diferença é registrada.
3. Se **passa da tolerância**, o fechamento fica **pendente de aprovação**: alguém com `caixa:aprovar_diferenca` confere, escreve o motivo e aprova (ou pede recontagem). O valor que cada aprovador pode aprovar é um **limite** da permissão (ex.: gerente até R$ 50, dono sem limite).
4. **Separação de funções (configurável):** por padrão, quem fechou não aprova a própria diferença. Em negócios pequenos, onde só existe o dono, a organização pode **permitir autoaprovação**, que continua auditada.
5. Toda diferença aparece em relatório por operador e por período, para enxergar padrões.

O inventário funciona igual: a contagem gera uma lista de **divergências** contra o saldo do sistema; as dentro da tolerância por insumo são ajustadas na hora, as maiores aguardam `estoque:inventario_aprovar`, e cada ajuste vira um movimento de estoque com motivo.

### Permissões derivadas
```
caixa:ver                   caixa:abrir
caixa:suprimento [A][L]     caixa:sangria [A][L]
caixa:fechar                caixa:fechar_cego
caixa:aprovar_diferenca [A][L]    caixa:reabrir [A]
caixa:ver_todos             (sem isso, só o próprio turno)
caixa:configurar_janela [A]       caixa:registrar_fora_do_horario [A]
```

---

## F6. Cozinha e bar (praças, impressão, KDS)

No escopo do produto; o faseamento fica para o roadmap.

- **Praça/destino**: cada produto pertence a uma ou mais praças (cozinha, bar, copa). A unidade configura as praças.
- **Envio de pedido**: lançar item gera um **pedido de preparo** roteado à praça, por impressora e/ou tela (KDS).
- **Ciclo do item**: `lançado → enviado → em preparo → pronto → entregue` (ou `cancelado`). Estados usados dependem da configuração da unidade.
- **KDS**: fila por praça, tempos, prioridade, marcar pronto, chamar garçom.
- **Cancelamento após envio**: avisa a praça; exige permissão.
- Arquitetura: itens publicam eventos; impressão e KDS são consumidores independentes.

```
cozinha:ver_fila       cozinha:marcar_preparo   cozinha:marcar_pronto
cozinha:reenviar_pedido   cozinha:configurar_pracas [A]
impressora:gerenciar [A]
```

---

## F7. Comprovantes e fiscal

- **Agora (planejado para a primeira entrega):** comprovante/pré-conta impressos (não fiscal) em impressora térmica ou PDF, com o modelo configurável por unidade.
- **Preparado, não construído:** emissão fiscal (NFC-e). A arquitetura reserva:
  - Dados fiscais no cadastro: unidade (CNPJ, regime, certificado, CSC) e produto (NCM, CFOP, CST/CSOSN, alíquotas).
  - **Documento fiscal** como entidade própria, ligada à comanda/pagamentos, com estados (`pendente → autorizado/rejeitado/contingência/cancelado`).
  - Emissão **assíncrona e idempotente** via adapter de provedor; contingência e reenvio previstos.
  - Retenção legal dos XMLs.

```
fiscal:ver     fiscal:emitir     fiscal:cancelar_nota [A]
fiscal:configurar [A]            comprovante:reimprimir
```

---

## F8. Financeiro

### Conceitos
- **Contas a pagar**: obrigações (compras de fornecedores, aluguel, folha, impostos, despesas avulsas). Vencimento, parcelas, recorrência, anexos, categoria de despesa, centro de custo (unidade).
- **Contas a receber**: valores a receber (fiado de clientes, repasses de operadoras de cartão/vale conforme prazo do método de pagamento, vouchers corporativos).
- **Lançamento financeiro**: registro imutável (partida única na v1; modelo comporta evolução para partida dobrada). Pagamento de uma conta gera movimento no caixa/conta bancária correspondente.
- **Contas financeiras**: caixa físico, conta bancária, carteira de operadora. Saldo derivado dos lançamentos.
- **CMV**: custo dos insumos consumidos pelas vendas (ficha técnica × custo congelado), mais perdas/cortesias separadas.
- **DRE gerencial**: receita, CMV, despesas, resultado por período e por unidade.

### Capacidades
| # | Capacidade | Detalhe |
|---|---|---|
| F8.1 | Contas a pagar | Criar, parcelar, recorrência, anexar documento, pagar (total/parcial), cancelar. |
| F8.2 | Geração a partir de compra | Compra recebida gera conta a pagar (parcelas e vencimentos). |
| F8.3 | Contas a receber | Fiado, repasses de operadora (com taxa descontada), conciliação. |
| F8.4 | Contas financeiras | Saldos e transferências entre contas. Importação de extrato por arquivo é opcional e de fase tardia; **Open Finance fora de escopo**. |
| F8.5 | Categorias e centros de custo | Plano de contas simples e configurável. |
| F8.6 | Fluxo de caixa | Realizado e projetado por vencimento. |
| F8.7 | CMV e margem | Por produto, categoria, período, unidade. |
| F8.8 | DRE gerencial | Por período e unidade, com comparação entre períodos. |
| F8.9 | Alertas | Vencimentos próximos/atrasados, fiado vencido. |

```
financeiro:ver               financeiro:ver_dre
conta_pagar:criar            conta_pagar:editar        conta_pagar:pagar [A][L]
conta_pagar:cancelar [A]     conta_receber:ver         conta_receber:baixar [A]
conta_financeira:gerenciar [A]   conta_financeira:transferir [A][L]
financeiro:categorias_gerenciar  financeiro:exportar
```

---

## F9. Equipe e comissões

- **Colaboradores**: cadastro de garçons e demais funcionários operacionais, com ou sem usuário de acesso ao sistema. Vínculo a uma ou mais unidades.
- **Comissão (regra simples)**: percentual sobre as vendas atribuídas ao garçom, configurável por organização/unidade, com possibilidade de percentual por garçom. Sem regras por produto, meta ou rateio nesta fase; o modelo guarda a regra aplicada em cada venda para permitir evolução sem reescrever o histórico.
- **Cálculo**: gerado no fechamento da comanda sobre o valor-base (configurável: com ou sem taxa de serviço/descontos). Cortesias e brindes seguem a regra definida (por padrão, não comissionam). Cancelamentos e estornos revertem a comissão.
- **Estatísticas do colaborador** (vendas, ticket médio, comissão acumulada e paga, por período).
- **Gestão de comissão:** marcar como paga individualmente, ajustar valor com motivo, excluir lançamento (auditado).
- **Pagamento de comissão**: acumula como dívida em aberto até ser paga; pagar gera conta a pagar/lançamento. Fechamento por período opcional.

```
equipe:ver                   equipe:colaborador_gerenciar
comissao:ver_proprias        comissao:ver_todas
comissao:configurar [A]      comissao:pagar [A]       comissao:ajustar [A][L]
```

---

## F10. Relatórios, dashboard e auditoria

### Relatórios
Todos filtráveis por **período, unidade(s), operador, método de pagamento, categoria/produto**, com exportação (CSV/XLSX/PDF) e agendamento futuro.
- **Vendas**: por dia/hora/dia da semana, ticket médio, por garçom, por produto/categoria, por método de pagamento, cortesias/brindes/descontos/cancelamentos.
- **Histórico diário completo**: toda venda, de todo dia, consultável e imutável.
- **Produtos**: ranking, curva ABC, margem, vendidos vs sem giro, entradas e saídas.
- **Estoque**: extrato de movimentações (entradas e saídas por insumo), posição em data passada, perdas, divergências de inventário, giro.
- **Caixa**: turnos, sangrias/suprimentos, diferenças por operador.
- **Financeiro**: contas a pagar/receber, fluxo de caixa, DRE, CMV.
- **Equipe**: vendas e comissões por colaborador.
- **Relatórios nomeados do sistema atual (todos mantidos):** vendas do dia, histórico de comandas, fechamento de caixa, DRE, CMV por produto, perdas e cortesias, vendas por garçom, produtos mais vendidos, pico de vendas por horário, vendas por produto.
- **Comparativos**: período contra período, unidade contra unidade (para organizações multi-unidade).
- **Engenharia de cardápio**: matriz de **popularidade × margem** por produto (estrela, vaca leiteira, abacaxi, cão), com sugestão de ação (destacar, reajustar preço, rever ficha, retirar). Usa dados que o sistema já tem (vendas, preço, custo da ficha).
- **CMV teórico × real**: compara o consumo previsto pelas fichas técnicas com o consumo apurado pelo inventário (saldo inicial + entradas − saldo contado). A diferença por insumo e por período aponta desperdício, erro de porcionamento, furto ou ficha desatualizada. Depende do inventário (F3.6).

**Retenção:** o sistema guarda todo o histórico; o custo de armazenamento é aceito. Dados históricos são imutáveis, e relatórios pesados usam estruturas pré-agregadas/réplica de leitura para não degradar a operação.

### Dashboard
- **Indicadores rápidos** do dia operacional: faturamento, comandas abertas, ticket médio, caixa aberto, comparativo com ontem/semana anterior.
- **"A fazer" / pendências acionáveis, por usuário e permissão:** estoque abaixo do limite, contas vencendo/atrasadas, caixa aberto há muito tempo, comandas esquecidas, divergências a aprovar, compras a receber, comissões a pagar, alertas da plataforma.
- Cada pendência leva direto à tela de resolução e só aparece para quem tem permissão de agir.
- Widgets configuráveis por perfil.
- **Widgets herdados do sistema atual (todos mantidos):** faturamento de hoje, de ontem, do mês e do mês anterior; ticket médio; CMV e lucro estimado do dia; comandas abertas (lista com tempo aberto) e fechadas hoje; faturamento por hora; mapa de calor do mês; formas de pagamento do dia; garçons do dia e top garçons; top produtos por faturamento com CMV%; comissões a pagar; cortesias e perdas do mês; estoque em atenção (críticos e menor estoque); compras agendadas; histórico diário e resumo anual.

### Auditoria e logs
- **Log de auditoria imutável** de toda ação sensível: quem, quando, onde (unidade/dispositivo/IP), o quê, valores antes/depois, motivo. Cobre todas as ações marcadas **[A]** e também logins, mudanças de permissão, exportações de dados e acessos negados.
- **Consulta e filtros** por usuário, ação, entidade e período; exportável.
- **Linha do tempo por entidade** (comanda, insumo, conta): histórico completo de eventos.
- **Log técnico** (aplicação/erros) separado do log de auditoria de negócio, com correlação por id de requisição.
- Auditoria é à prova de adulteração (append-only, com encadeamento de integridade) e com retenção definida.

```
relatorio:ver_vendas         relatorio:ver_estoque     relatorio:ver_financeiro
relatorio:ver_custos         relatorio:exportar [A]    relatorio:ver_todas_unidades
dashboard:ver                dashboard:configurar
auditoria:ver                auditoria:exportar [A]
```

---

## F11. Identidade e acesso (sistema dos clientes)

### Autenticação
- **E-mail e senha** sempre disponível. Senha forte, verificação de vazamento (lista de senhas comprometidas), hash moderno (Argon2id), bloqueio progressivo contra tentativa em massa.
- **Login com Google** (custo zero): só com e-mail verificado; vincular a uma conta existente exige confirmação, nunca automática. Outros provedores sociais ficam para depois.
- **2FA: fora do escopo por enquanto.** O modelo de autenticação já reserva o encaixe (fator adicional por usuário, política "exigir 2FA" por organização) para entrar depois por app autenticador (TOTP, sem SMS) sem refazer login nem sessões. Ver riscos aceitos em `01-visao`.
- **Sessões**: token curto + refresh rotativo, lista de sessões ativas e revogação, encerramento ao trocar senha ou perder permissão, alerta de login em dispositivo novo.

### Login rápido no salão (PIN) — recomendado
Garçom digitando e-mail e senha toda hora é inviável. Recomendação:
- O **dispositivo do salão é registrado** por um gerente/dono (autenticado com senha + 2FA) e vinculado a uma unidade.
- Nesse dispositivo, o colaborador entra com **PIN pessoal** (4 a 6 dígitos), que só vale ali, naquela unidade.
- PIN dá acesso **somente às permissões operacionais** do perfil, nunca a administração, dados financeiros ou exportações.
- Limite de tentativas com bloqueio; o PIN é guardado com hash; o dono ou gerente pode redefinir.
- Dispositivo pode ser revogado remotamente; sessão de PIN expira por inatividade.

### Usuários e convites
Dois tipos de conta, conforme o papel:
- **Conta com e-mail** (dono, gerentes, administrativo): convite por link de uso único; a pessoa **define a própria senha**. Pode entrar com Google.
- **Conta operacional sem e-mail** (garçom, caixa e demais): **usuário + senha ou PIN**, sem precisar de e-mail. O **nome de usuário é único dentro da organização** (login informa organização + usuário). O dono/gerente cria a conta e o sistema gera uma **credencial temporária mostrada uma única vez**, com **troca obrigatória no primeiro acesso**. Ninguém define a senha de outra pessoa.
- Contas operacionais têm permissões limitadas por padrão: sem administração, sem dados financeiros, sem exportação (configurável por perfil).
- Redefinição: quem tem `usuario:redefinir_acesso` gera nova credencial temporária; contas com e-mail podem usar "esqueci a senha".
- Desativar usuário (sem apagar histórico), reativar, forçar logout.
- Regras de proteção: dono não pode ser desativado nem rebaixado por outro; ninguém altera as próprias permissões; sempre deve existir ao menos um dono.

### Modelos de permissão (templates)
A plataforma oferece **modelos prontos** (ex.: gerente, caixa, garçom, estoquista) e cada organização pode criar os seus. Um perfil criado a partir de um modelo pode **acompanhar o modelo** (mudou o modelo, mudam os perfis vinculados) ou ser **copiado e editado livremente**. Escolha explícita no momento da criação. Perfis de fábrica não são editáveis, só duplicáveis.

### Modelo de permissões (resumo do que já foi decidido)
- Permissões granulares `recurso:ação`, agrupadas em **perfis** customizáveis (com perfis de fábrica como ponto de partida).
- **Escopo**: organização inteira ou unidades específicas.
- **Condições e limites** por permissão (valor, %, horário, tipo de dispositivo).
- **Deny explícito** vence allow. **Overrides individuais** além do perfil.
- Permissões **derivadas do catálogo de features**: nova feature declara as suas, aparece na tela de perfis sozinha.
- Toda decisão sensível e toda mudança de permissão é auditada.
- Simulador "o que este usuário pode fazer?" para quem administra.

```
usuario:ver   usuario:convidar [A]   usuario:desativar [A]   usuario:redefinir_acesso [A]
perfil:ver    perfil:gerenciar [A]   permissao:atribuir [A]
dispositivo:registrar [A]   dispositivo:revogar [A]
sessao:ver_todas   sessao:revogar [A]
organizacao:configurar [A]  unidade:gerenciar [A]
seguranca:exigir_2fa [A]
```

---

## F12. Control Tower (plataforma)

Painel em `/control`. **Separação lógica, não duas infraestruturas**: a Control Tower roda no mesmo repositório e no mesmo deploy do sistema (monólito modular), mas com **domínio de identidade próprio**: tabelas e esquema separados de administradores, sessões e tokens com audiência distinta, permissões próprias e, de preferência, host separado (ex.: `control.<domínio>`). Nenhum login de cliente acessa a Control Tower e vice-versa. Se um dia for preciso, o módulo pode ser extraído para um deploy próprio sem mudar o modelo. Não exige infraestrutura adicional agora.

### Administradores
- **Super admin** (você): cria e remove administradores, define suas permissões. Conta protegida: não pode ser removida nem rebaixada por outros; recuperação por procedimento controlado. Sem restrições de impersonação.
- **Administradores delegados** com permissões granulares da plataforma (mesmo rigor de modelo: granularidade, escopo, limites).
- 2FA fora do escopo por enquanto (ver riscos aceitos).
- Escopo por organização é possível (ex.: suporte vê só as organizações atribuídas).

### Capacidades
| # | Capacidade | Detalhe |
|---|---|---|
| F12.1 | Visão geral | Saúde da plataforma, organizações ativas, uso, receita, alertas. |
| F12.2 | Organizações | Criar, editar, suspender, reativar, excluir (com retenção); ver unidades e usuários. |
| F12.3 | Planos e cobrança | Planos, limites (nº de unidades/usuários), assinatura, inadimplência, bloqueio por falta de pagamento. |
| F12.4 | Feature flags | Liberar/retirar features por organização, plano ou unidade. |
| F12.5 | Comunicados | Avisos in-app para todas ou segmentos de organizações. |
| F12.6 | Suporte com impersonação | Ver abaixo. |
| F12.7 | Auditoria da plataforma | Todo ato de administrador, imutável, filtrável. |
| F12.8 | Administradores | Gestão de contas e permissões da Control Tower. |
| F12.9 | Limites e abuso | Rate limits, bloqueio de organização, investigação de incidentes. |
| F12.10 | Backups e operação | Painel de jobs, filas, restauração (visão técnica). |
| F12.11 | Ciclo da assinatura | Estados, teste personalizável, bloqueios e telas informativas; ver **Ciclo da assinatura** abaixo. |
| F12.12 | Planos e limites | Catálogo de planos com preço mensal, limite de usuários e de unidades por plano, vínculo à assinatura, troca de plano. |
| F12.13 | Gestão de usuários e perfis das organizações | Criar e editar usuários de qualquer organização, redefinir acesso, listar perfis e aplicar modelos de permissão. |
| F12.14 | Cockpit da plataforma | Métricas globais e por organização (uso, comandas, faturamento, usuários ativos, assinatura), com paginação e filtros. |
| F12.15 | Configurações da plataforma | Parâmetros globais (ex.: duração do período de teste, textos, limites padrão) versionados e auditados. |
| F12.16 | Comunicados com segmentação | Alvo todas as organizações ou selecionadas, expiração, acompanhamento de leitura por usuário. |
| F12.17 | Unidades da organização | A plataforma **define quantas unidades** a organização pode ter (pelo plano ou por limite próprio) e pode **criar as unidades em nome dela**; o dono cria as demais até o limite. Aumentar o limite é ação da plataforma, auditada. |

### Ciclo da assinatura (estados definidos na Control Tower)
Cinco estados, simples de entender e de operar:

| Estado | Para que serve | Acesso dos usuários |
|---|---|---|
| **Em teste** | Período experimental com **duração personalizável por organização** (ex.: 7, 30, 60, 90 dias ou data final); pode ser prorrogado | Total, com faixa informando quanto falta |
| **Ativa** | Cliente regular. Tem **tolerância de atraso configurável** (ex.: 5 dias): vencida dentro da tolerância continua ativa, com avisos ao dono; passando dela, vira Suspensa automaticamente | Total |
| **Suspensa** | Pausa temporária (inadimplência após a tolerância, ou decisão do administrador). Reversível | **Escolhido em cada suspensão:** só leitura e exportação, ou bloqueio total; tela informativa com o motivo e como regularizar |
| **Bloqueada** | Bloqueio por segurança, abuso, fraude, questão legal ou rescisão. Só o super admin libera | Nenhum acesso, nem do dono; tela neutra |
| **Encerrada** | Fim de vida. Chega por cancelamento do cliente (ao fim do período pago), por bloqueio mantido ou por suspensão longa; dados retidos pelo prazo legal e depois eliminados/anonimizados | Janela configurável só de leitura e exportação, depois nenhum |

**Cancelamento e atraso não são estados:** são ações e prazos. "Cancelar" agenda o encerramento para o fim do período pago; "tolerância" é um prazo dentro da Ativa.

**Transições válidas:** `em_teste → ativa | suspensa | encerrada`; `ativa → suspensa | bloqueada | encerrada`; `suspensa → ativa | bloqueada | encerrada`; `bloqueada → ativa | suspensa` (só super admin) `| encerrada`; `encerrada` é final. Transição inválida é recusada.

**Ações do administrador** (cada uma com **motivo obrigatório**, histórico com autor e horário, e aviso ao dono):
- Iniciar teste com N dias; **prorrogar** por N dias; **converter** em ativa.
- Conceder **período gratuito/cortesia** por prazo.
- Registrar pagamento (manual ou por integração).
- **Agendar** mudança de estado para uma data (ex.: suspender em 05/12 se não pagar).
- **Exceção manual:** manter liberado até uma data apesar do vencimento.
- Suspender (definindo leitura ou bloqueio total), bloquear, reativar, cancelar (agenda o encerramento).
- Definir a tolerância de atraso e o que acontece ao fim do teste (virar suspensa ou ativa com tolerância); padrão da plataforma, sobrescrevível por organização.

**Regras:**
- **O estado é decidido no servidor** em toda requisição; o front só mostra a tela informativa correspondente.
- **Impersonação** e acesso da plataforma continuam possíveis em qualquer estado, auditados.
- **Exportação de dados** garantida pela LGPD em todos os estados até a eliminação.
- Toda mudança notifica o dono (F13, obrigatória) e fica no histórico.
- Telas informativas por estado: teste acabando, tolerância, suspensa (leitura ou total), bloqueada, encerrada; texto configurável e contato de suporte.

```
assinatura:ver               assinatura:iniciar_teste [A]     assinatura:prorrogar_teste [A][L]
assinatura:converter [A]     assinatura:conceder_cortesia [A][L]
assinatura:registrar_pagamento [A]   assinatura:agendar_mudanca [A]
assinatura:excecao_manual [A][L]     assinatura:suspender [A]
assinatura:bloquear [A]      assinatura:reativar [A]
assinatura:cancelar [A]      assinatura:ver_historico
assinatura:configurar_politicas [A]   (padrões de teste, tolerância, fim de teste)
```

### Impersonação (suporte)
Entrar na organização como um usuário dela, para dar suporte. Regras:
- **Super admin**: pode tudo, sem exigência de motivo e sem limite de tempo.
- **Administradores delegados**: o super admin configura, **por administrador**, a política de impersonação:
  - somente leitura ou leitura e escrita;
  - tempo máximo da sessão (ou sem limite);
  - motivo/ticket obrigatório ou não;
  - em quais organizações pode atuar.
- **Sempre, sem configuração:**
  - a organização é **informada**: o acesso aparece no log de auditoria da organização ("administrador da plataforma acessou em tal horário");
  - todo ato é **atribuído ao administrador real**, nunca ao usuário imitado;
  - a sessão de impersonação fica identificada na tela;
  - registro imutável no log de auditoria da plataforma.

```
plataforma:ver    organizacao:ver   organizacao:criar [A]   organizacao:suspender [A]
organizacao:excluir [A]   plano:gerenciar [A]   cobranca:ver   cobranca:ajustar [A][L]
feature_flag:gerenciar [A]   comunicado:publicar [A]
suporte:impersonar [A]   suporte:impersonar_escrita [A]   suporte:impersonar_politica_configurar [A]
admin_plataforma:gerenciar [A]   auditoria_plataforma:ver
```

---

## F13. Notificações

Camada única que transforma **eventos do sistema** em avisos para as pessoas certas, no canal certo. Alertas de estoque (F3), pendências do dashboard (F10), pedido pronto na cozinha (F6), vencimentos (F8) e comunicados da plataforma (F12) usam todos esta mesma camada, sem cada módulo reinventar a sua.

### Modelo
`evento de domínio → regra → destinatários → canais → entrega`
- **Evento**: algo que aconteceu (insumo cruzou o limite, caixa aberto há 14 h, pedido pronto, conta venceu, login em dispositivo novo).
- **Regra**: define quando notificar, severidade, destinatários e canais. Regras de fábrica por tipo de evento, ajustáveis por organização e unidade.
- **Destinatários** resolvidos por **permissão e escopo**, nunca por lista fixa: só recebe quem tem permissão de agir naquela unidade. Pode-se acrescentar usuários específicos ou grupos (por perfil).
- **Canais**:
  - **In-app** (sempre): central de notificações com contador, lida/não lida, link direto para resolver.
  - **Push em tempo real** no navegador/app (SSE ou WebSocket) para avisos urgentes, sem polling.
  - **E-mail**: resumos e avisos não urgentes.
  - **WhatsApp/SMS**: depois, por provedor; têm custo por mensagem, então ficam atrelados a plano/limite.
- **Entrega**: fila assíncrona com tentativas, estado por canal (`enfileirada → enviada → entregue/falhou`), idempotência (o mesmo evento não notifica duas vezes) e registro de entrega.

### Capacidades
| # | Capacidade | Detalhe |
|---|---|---|
| F13.1 | Central de notificações | Lista, filtros, marcar lida/todas, arquivar, link de ação. |
| F13.2 | Tempo real | Aviso instantâneo no app aberto; reconexão sem perda (recupera pendentes). |
| F13.3 | Regras por organização/unidade | Ligar/desligar tipos, limiares, severidade, destinatários, canais. |
| F13.4 | Preferências pessoais | Canais e tipos por usuário, dentro do que a organização permite. |
| F13.5 | Antirruído | Dispara na transição (não a cada ocorrência), agrupamento, silenciar tipo/insumo, janela de silêncio, resumos diários/semanais. |
| F13.6 | Severidade | `info`, `atenção`, `crítico`; crítico ignora silêncio e não pode ser desligado se for de segurança. |
| F13.7 | Notificações obrigatórias | Segurança (login novo, senha/permissão alterada, impersonação), cobrança e comunicados críticos da plataforma: sempre entregues. |
| F13.8 | Modelos de mensagem | Textos por tipo, idioma, variáveis; plataforma define o padrão, organização personaliza o permitido. |
| F13.9 | Entregas e custos | Histórico de entrega por canal, falhas, consumo de mensagens pagas. |
| F13.10 | Comunicados | Mensagens da plataforma a todas ou a segmentos de organizações (F12.5), pela mesma camada. |

### Fontes previstas de notificação
- **Estoque:** abaixo do limite configurado por insumo, estoque negativo, divergência de inventário.
- **Comandas e salão:** comanda aberta há muito tempo, cancelamento/cortesia acima do limite, pedido pronto na cozinha (para o garçom responsável), item recusado.
- **Caixa:** aberto além da janela, diferença no fechamento a aprovar.
- **Financeiro:** conta a vencer/vencida, fiado vencido, repasse de operadora atrasado, comissões a pagar.
- **Compras:** pedido a receber, recebimento com divergência.
- **Segurança:** login em dispositivo novo, falhas repetidas, mudança de permissões, impersonação, exportação de dados.
- **Plataforma:** comunicados, fatura, limites do plano, manutenção programada.

### Requisitos
- **Isolamento**: notificação pertence a uma organização e nunca vaza entre organizações (mesma garantia de qualquer outro dado).
- **Sem dado sensível no corpo** de e-mail/WhatsApp (valores e dados de clientes só dentro do app).
- **Desempenho**: criar uma notificação nunca atrasa a operação que a originou (assíncrono, fora da transação crítica; o evento é gravado na transação, a entrega vem depois).
- **Retenção** definida (ex.: in-app por N dias, log de entrega por mais tempo).

```
notificacao:ver_proprias          notificacao:preferencias_editar
notificacao:regras_configurar [A] notificacao:modelos_editar [A]
notificacao:destinatarios_definir [A]
notificacao:ver_entregas          notificacao:canal_pago_configurar [A][L]
comunicado:publicar [A]           (plataforma, F12)
```

### Decisões fechadas
- Canais da primeira entrega: in-app + push em tempo real + e-mail. WhatsApp/SMS depois.
- Construtor de regras próprias fica para fase posterior. **O que entra já no dia zero** é o que o torna barato depois: todo evento de domínio é **estruturado e versionado** (tipo, entidade, unidade, valores, ator), e as regras de fábrica já são avaliadas por um motor genérico de condições. O construtor depois é só uma tela sobre esse motor.

### Apresentação in-app: bolinhas flutuantes (direção do dono, rascunho)
O in-app não é uma página escondida no menu: é **um conjunto de bolinhas flutuantes no canto inferior, presentes em qualquer tela**, cada uma com **número** de itens pendentes (como o chat de IA do Notion). A referência visual são esferas pequenas com um selo de contagem, um selo de texto ("Novo") ou um ponto de estado.
- **Duas famílias de notificação**, separadas por tipo:
  - **Plataforma** (padrão, menos urgentes): comunicados, fatura e plano, manutenção programada, novidades.
  - **Operação** (do dia a dia do restaurante): subdivididas em **ações para fazer** (algo espera uma decisão ou tarefa: aprovar cancelamento, repor insumo, receber pedido) e **avisos** (algo aconteceu: comanda fechada, estoque de carne acabando, pagamento recebido).
- **Uma bolinha por grupo**, cada uma com **cor própria e contagem própria** (proposta inicial: laranja = precisa de ação, azul = informativo/plataforma, verde = concluído ou positivo). **Cor nunca é o único sinal**: cada bolinha e cada item também têm ícone e rótulo em texto (acessibilidade e daltonismo).
- **Por perfil de permissão** (F13, destinatários por permissão e escopo): o que aparece e quais bolinhas existem depende de quem é o usuário. Um garçom vê "pedido pronto" e "comanda parada"; o administrador vê também cancelamento acima do limite, caixa com diferença, estoque e financeiro. Um usuário sem notificações de um grupo não vê a bolinha dele.
- **Convive com a bolinha de lançamento rápido** (ver `05-ux-design`): no produto real são a de **lançar** e as de **notificação**. A bolinha de **configuração** do protótipo **não existe no produto**.
- Cada item abre a tela de origem (link de ação) e o painel da bolinha oferece filtro, "marcar todas como lidas" e silenciar tipo (F13.5).
- **Celular (decidido: "como for recomendado", o dono delegou):** uma bolinha só de notificações, mostrando o **total** e **pontinhos coloridos** com a contagem de cada grupo; ao tocar, o painel abre com **abas por grupo** (Ações, Avisos, Plataforma). A bolinha de **lançar** fica separada. Ficam no máximo duas bolinhas visíveis, acima da barra de ações, para não cobrir os botões. No computador, uma bolinha por grupo.
- **Em aberto:** significado final das cores; se a organização pode renomear ou recolorir grupos.

### Custo do construtor de regras próprias
- **Custo de execução: baixo.** Avaliar uma regra sobre um evento é uma comparação simples; não pesa na operação.
- **Custo de desenvolvimento: moderado, e quase todo é da interface**, não do motor. Se os eventos já nascem estruturados (decisão acima), o motor é pequeno. Se não, depois sai caro (refazer todos os eventos).
- **Riscos a tratar** quando for construído: regra que dispara demais (limite de frequência por regra), regra que notifica quem não tem permissão (sempre filtrada por permissão e escopo), e canais pagos (WhatsApp) com teto de consumo.
- **Recomendação:** projetar eventos e motor agora (custo próximo de zero), entregar o construtor numa fase posterior.

### Catálogo de situações notificáveis (mapa para o futuro)
Tudo abaixo é candidato; nem tudo entra na primeira entrega. Marcado: **F** = fábrica, **R** = disponível via regra própria, **P** = precisa de módulo ainda não construído.

**Estoque**
- Insumo abaixo do limite configurado (F) · estoque zerado (F) · estoque negativo (F) · reposição recebida que resolve alerta (F)
- Insumo sem movimentação há N dias (R) · divergência de inventário acima de X (F) · perda/quebra acima de X no período (R)
- Produto ficou indisponível por falta de insumo da ficha (F) · custo médio subiu mais de X% (R) · lote perto do vencimento (P)

**Comandas e salão**
- Comanda aberta há mais de N horas (F) · mesa sem lançamento há N minutos (R)
- Desconto/cortesia/brinde/cancelamento acima do limite (F, vai ao gerente) · mesmo garçom com muitos cancelamentos (R)
- Cliente pediu a conta · chamada de garçom (P) · comanda com saldo pendente após pagamento parcial (F)
- Pagamento recusado ou pendente de confirmação (F) · estorno realizado (F)

**Cozinha e bar** (P)
- Pedido novo na praça · pedido pronto para o garçom · pedido atrasado além do tempo-alvo · item cancelado após envio · praça sem impressora/tela online

**Caixa e operação**
- Caixa aberto além da janela (F) · operação prestes a fechar (aviso N min antes) (F) · fechamento do dia pendente (F)
- Diferença de caixa acima da tolerância (F) · sangria acima do limite (F) · lançamento fora do horário permitido (F)
- Operação aberta sem caixa aberto (F) · início do dia operacional / resumo de abertura (R)

**Compras e fornecedores**
- Pedido de compra aguardando recebimento (F) · recebimento com divergência (F) · NF-e importada pendente de conferência (F)
- Preço de compra acima da média (R) · fornecedor atrasou entrega (R)

**Financeiro**
- Conta a vencer em N dias / vencida (F) · fiado vencido ou acima do limite do cliente (F) · repasse de operadora atrasado (F)
- Saldo de conta financeira abaixo do mínimo (R) · projeção de fluxo de caixa negativa (R) · comissões acumuladas a pagar (F)
- Margem de produto abaixo do mínimo (R) · CMV do período acima da meta (R)

**Equipe**
- Colaborador sem acesso há N dias (R) · convite/credencial temporária expirando (F) · novo dispositivo registrado (F)

**Segurança e conta** (obrigatórias)
- Login em dispositivo novo · falhas repetidas de login · senha alterada · permissões alteradas · perfil ou usuário desativado
- Impersonação iniciada por administrador da plataforma · exportação de dados · acesso negado repetido a recurso sensível

**Metas e resumos** (R)
- Resumo diário/semanal de vendas · meta de faturamento atingida ou em risco · recorde de vendas · queda relevante vs mesmo dia da semana anterior

**Plataforma**
- Comunicado · manutenção programada · fatura gerada, vencendo, vencida · limite do plano próximo · feature liberada · suspensão por inadimplência · incidente em andamento

**Sistema**
- Falha de integração (impressora, provedor de pagamento, fiscal) (F) · fila de entrega com falhas (plataforma) · backup/restauração concluído (plataforma)

---

## F14. Configurações da organização e da unidade

### Princípio: configuração em camadas
`padrão da plataforma → organização → unidade`. Cada nível herda do anterior e pode sobrescrever **só o que o nível acima permitir**. Para cada opção, a organização escolhe se a unidade pode mudá-la ("travar" ou "liberar"). Toda alteração é auditada (antes/depois) e passa pela permissão correspondente.

### Organização
- **Dados cadastrais:** razão social, nome fantasia, documento, contato, logotipo.
- **Unidades:** criar, editar, desativar; vincular usuários e perfis a unidades.
- **Segurança:** política de senha, duração de sessão, bloqueio, regras de dispositivo/PIN, "exigir 2FA" (reservado), login com Google liberado ou não.
- **Padrões herdados pelas unidades:** estoque negativo, regras de comissão, taxa de serviço, métodos de pagamento, janela operacional, limites e condições de permissão, regras de notificação, política de retenção dentro do plano.
- **Catálogos compartilhados:** cardápio, insumos e fornecedores podem ser **da organização** (compartilhados entre unidades) ou **da unidade**; preço e disponibilidade podem ser sobrescritos por unidade.
- **Plano e cobrança:** plano atual, limites e consumo, faturas (somente leitura; mudança de plano via plataforma ou autoatendimento futuro).
- **Dados e privacidade:** exportação, solicitações de titulares (LGPD), contatos de privacidade.

### Unidade
> **Pequenos negócios:** o administrador (dono) da organização pode configurar tudo, inclusive o que é da unidade, sem precisar de gerente. Quem edita a configuração de cada unidade é uma **permissão** (`configuracao:unidade_editar`), concedida por padrão ao dono e ao gerente, e que o dono pode dar ou tirar de qualquer perfil.

- **Cadastro:** nome, endereço, documento/inscrições, contato, fuso horário, moeda.
- **Operação:** janela operacional e dia operacional, horários de funcionamento, regras de registro por horário (F5), numeração/identificação de comandas, **uso de mesas (liga/desliga)**, áreas e **mapa de mesas** (salão, varanda, balcão).
- **Venda:** taxa de serviço e couvert, regras de desconto e arredondamento, métodos de pagamento ativos e suas taxas, política de estoque negativo (se a organização liberou).
- **Caixa:** número de caixas, conferência cega, tolerância de diferença, sangria máxima.
- **Cozinha e impressão:** praças, impressoras, modelos de comprovante, tempo-alvo de preparo, destinos por produto.
- **Estoque:** locais, limites de alerta padrão, política de baixa.
- **Fiscal (preparado):** CNPJ e regime, certificado, séries, CSC (dados sensíveis criptografados).
- **Dispositivos:** terminais registrados, PIN, revogação (F11).
- **Notificações:** regras e destinatários da unidade (F13).
- **Aparência:** nome exibido, modelo e logotipo no comprovante.

### Capacidades
| # | Capacidade | Detalhe |
|---|---|---|
| F14.1 | Edição em camadas | Herança, sobrescrita, travar/liberar por opção. |
| F14.2 | Histórico de alterações | Quem mudou, quando, valor anterior; restaurar versão anterior. |
| F14.3 | Aplicação segura | Mudanças com efeito operacional (janela, taxas, estoque negativo) valem a partir de um momento definido, nunca retroativas sobre vendas já feitas. |
| F14.4 | Validação | Impede combinações inválidas (ex.: janela sobreposta, taxa fora de faixa). |
| F14.5 | Assistente de configuração inicial | Roteiro guiado para uma organização nova: unidade, cardápio básico, métodos, usuários, perfis. |
| F14.6 | Modelos de unidade | Duplicar configuração de uma unidade para criar outra. |
| F14.7 | Comparar unidades | Ver diferenças de configuração entre unidades. |

```
configuracao:ver            configuracao:organizacao_editar [A]
configuracao:unidade_editar [A]   configuracao:travar_opcao [A]
configuracao:ver_historico  configuracao:restaurar [A]
unidade:criar [A][L]        unidade:desativar [A]
fiscal:configurar [A]       seguranca:politica_editar [A]
```

---

## F15. Calendário e agenda operacional

Um **calendário de verdade** da organização e da unidade, no estilo de um calendário pessoal/corporativo, que também mostra o que o sistema sabe que vai acontecer.

### Conceitos
- **Evento**: título, descrição, dia inteiro ou horário (início e fim), local/unidade, cor, responsável(is), lembretes.
- **Evento recorrente**: diário, semanal (dias da semana), mensal (dia do mês ou "primeira segunda"), anual, com data final ou número de ocorrências; **exceções** (pular uma ocorrência, editar só uma, editar esta e as seguintes, editar todas).
- **Camadas (calendários)**: cada tipo de item é uma camada que o usuário liga ou desliga e a organização configura:
  - eventos próprios (shows, jogos, reservas, feriados, manutenção, treinamentos);
  - **pagamentos**: contas a pagar e a receber por vencimento, com valor e situação (aberta, paga, atrasada);
  - **entregas de insumo** (compras agendadas: fornecedor e itens);
  - **promoções** vigentes (início, fim, recorrência);
  - **escalas e equipe** (quando existir), **caixa** (abertura e fechamento previstos), **assinatura** (vencimento, para o dono);
  - **notificações e lembretes** configurados para aparecerem no calendário (ex.: "conta vence", "reposição prevista", alertas do F13 com data).
- **Notificações no calendário (configurável):** cada tipo de notificação do F13 tem a opção "mostrar no calendário" (por organização, depois por usuário). Notificações com data (vencimentos, entregas, lembretes) viram marcadores; as sem data não aparecem.

### Capacidades
| # | Capacidade | Detalhe |
|---|---|---|
| F15.1 | Criar, editar, excluir eventos | Com campos acima, anexos e links. |
| F15.2 | Recorrência completa | Padrões e exceções, como em calendários convencionais. |
| F15.3 | Visões | Dia, semana, mês, agenda (lista) e linha do tempo por unidade. |
| F15.4 | Camadas e filtros | Ligar/desligar por tipo, unidade, responsável e cor. |
| F15.5 | Itens do sistema | Pagamentos, entregas, promoções etc., somente leitura, com atalho para a tela de origem. |
| F15.6 | Lembretes | Antes do evento, pelos canais do F13 (in-app, push, e-mail). |
| F15.7 | Arrastar para reagendar | Eventos próprios; itens do sistema só por ação na tela de origem (ex.: remarcar vencimento exige permissão financeira). |
| F15.8 | Convites e responsáveis | Atribuir colaboradores; confirmação de presença (opcional). |
| F15.9 | Compartilhamento | Calendário por unidade, por organização e pessoal; visibilidade por perfil. |
| F15.10 | Assinatura externa e exportação | Exportar/assinar em formato padrão (iCal) para Google Agenda e similares, somente leitura. |
| F15.11 | Sugestões de impacto | Evento pode sugerir ajuste (reforço de estoque, promoção, escala); só indicação, nunca automático. |

### Permissões
```
calendario:ver                     calendario:ver_de_outros
calendario:evento_criar            calendario:evento_editar
calendario:evento_excluir [A]      calendario:evento_recorrente_editar_serie
calendario:ver_pagamentos          (camada financeira; exige financeiro:ver)
calendario:ver_entregas            calendario:ver_promocoes
calendario:configurar_camadas [A]  calendario:configurar_notificacoes [A]
calendario:compartilhar            calendario:exportar_ical [A]
```

---

## F16. Clientes (base mínima, preparada para crescer)

Cadastro de clientes da organização, **mínimo hoje** e com encaixe para fidelidade, reservas e CRM no futuro. Justificativa: o **fiado já exige identificar o cliente** (F4.6), então o cadastro é necessário de qualquer forma.

### Conceitos
- **Cliente**: nome, contato (telefone, e-mail), documento (CPF/CNPJ, **opcional e só quando necessário**, como no fiado ou na nota fiscal), data de nascimento (opcional), observações, etiquetas.
- **Consentimento (LGPD)**: finalidade e data do consentimento registradas por cliente (atendimento, comunicação, fidelidade); retirar consentimento é possível a qualquer momento.
- **Minimização:** nenhum cadastro é obrigatório para vender. Cliente identificado entra só quando há motivo (fiado, nota com CPF, reserva, fidelidade).
- **Vínculos:** a comanda pode ter um **cliente opcional**; o fiado exige. A organização é a dona do cadastro, compartilhado entre as unidades.

### Capacidades
| # | Capacidade | Detalhe |
|---|---|---|
| F16.1 | Cadastro e busca | Criar, editar, buscar por nome, telefone ou documento; evitar duplicidade. |
| F16.2 | Histórico | Comandas, valor gasto, frequência, itens favoritos, fiado em aberto. |
| F16.3 | Consentimento | Registro por finalidade, revogação, comprovação. |
| F16.4 | Direitos do titular | Exportar, corrigir e anonimizar os dados do cliente sob demanda (RNF5). |
| F16.5 | Etiquetas e segmentos | Agrupar clientes (ex.: VIP, aniversariantes do mês). |
| F16.6 | Mesclar duplicados | Unir cadastros repetidos, auditado. |

### Preparado, não construído
Programa de fidelidade e cashback, reservas e fila de espera, campanhas por mensagem, pesquisa de satisfação. O modelo do cliente e o consentimento já suportam esses usos.

```
cliente:ver                   cliente:gerenciar
cliente:ver_dados_pessoais [A]   (documento, nascimento; mascarados sem isso)
cliente:ver_historico         cliente:mesclar [A]
cliente:exportar [A]          cliente:anonimizar [A]
```

---

## F17. Importação e exportação em massa

Trazer dados por **planilha** (CSV e Excel), para colocar um cliente novo no ar rápido e para migrar de outro sistema.

### Entidades importáveis
Produtos e categorias, insumos, fichas técnicas, fornecedores, clientes, mesas, métodos de pagamento personalizados, saldos iniciais de estoque.

### Como funciona
1. **Modelo de planilha:** cada entidade tem um **modelo para baixar** (colunas, exemplos, regras), em CSV e Excel, em português.
2. **Também aceita planilha livre:** o usuário envia qualquer planilha e **mapeia as colunas** para os campos; o mapeamento fica salvo para a próxima vez (útil ao migrar de outro sistema).
3. **Pré-visualização e validação antes de gravar:** mostra linha a linha o que será criado, atualizado ou rejeitado, com o motivo do erro (campo obrigatório, formato, duplicado). **Nada é gravado até o usuário confirmar.**
4. **Atualiza ou cria:** usa uma chave natural (código, EAN, CNPJ, nome) para decidir; modo "só criar", "criar e atualizar" ou "só atualizar".
5. **Execução assíncrona** pelo worker, com progresso, resultado e relatório de erros para baixar.
6. **Desfazer:** uma importação recém-feita pode ser revertida inteira por um período (registros criados saem; atualizados voltam ao valor anterior).
7. **Segurança:** limite de tamanho e de linhas, verificação do tipo real do arquivo, proteção contra fórmulas maliciosas (injeção de planilha), isolamento por organização, e tudo auditado.
8. **Exportação:** qualquer lista pode ser exportada para CSV/Excel com os filtros aplicados (permissão e auditoria, F10).

### Permissões
```
importacao:executar [A]      importacao:ver_historico
importacao:desfazer [A]      importacao:modelos_baixar
exportacao:executar [A]
```

### Ligação com a migração do sistema atual (D8)
O mesmo importador serve para trazer os dados do sistema antigo depois, sem módulo extra: exporta-se do legado em planilha e importa-se pelo mapeamento.

---

## F18. Gestão avançada (extras aceitos)

Funcionalidades que vão além do núcleo operacional e protegem margem e dinheiro. Todas reaproveitam dados que o sistema já tem. Origem: `02e` (brainstorm). Fora do escopo por decisão: checklists e controle sanitário, mural de recados, garrafeira, alérgenos, receita com passo a passo, ranking e metas de garçons.

### F18.1 Resumo do dia
- Gerado ao **fechar o dia operacional** (ou em horário configurável) e enviado por **e-mail, push e central in-app** (WhatsApp depois).
- Conteúdo: faturamento, ticket médio, número de comandas, CMV e lucro estimado, caixa (diferenças), formas de pagamento, estoque crítico, destaques e alertas, **comparado com o mesmo dia da semana anterior** e com a média das últimas quatro semanas.
- Por unidade e consolidado (organização). Destinatários definidos por permissão e preferência; conteúdo configurável. Sem dados pessoais de clientes no corpo do e-mail (F13).

### F18.2 Painel ao vivo ("como está a casa agora")
- **Pensado para o celular**: comandas abertas (e as paradas há muito tempo), faturamento da hora e do dia, ticket médio, caixa aberto, itens mais vendidos agora, estoque crítico, garçons ativos, alertas em aberto.
- Atualização em **tempo real** (mesmo canal do F13, sem polling). Por unidade e visão consolidada.
- Somente leitura, com atalhos para a tela de resolução quando o usuário tem permissão.

### F18.3 Alerta de margem e simulador de preço
- **Margem-alvo** por produto ou categoria (ou CMV-alvo).
- **Monitor de custo:** quando o custo médio de um insumo muda além de um limite, o sistema recalcula o custo dos produtos que o usam (pelas fichas) e **lista os que ficaram abaixo da margem-alvo**.
- **Sugestão de preço** que recupera a margem, com regra de arredondamento configurável (ex.: terminar em ,90).
- **Simulador:** "e se eu subir 5%?" mostra nova margem, CMV% e impacto no resultado por período, assumindo o volume atual e permitindo um cenário de queda de volume informado pelo usuário. Aplicar o preço exige permissão e fica no histórico de preços.
- Notificação de margem corroída (F13); alimenta a engenharia de cardápio (F10).

### F18.4 Controle de chope, doses e garrafas
Estende ficha técnica e estoque para bebidas.
- **Unidades de volume** (ml, litro) e **tamanhos/variantes** do produto (copo de 300 ml e 500 ml, dose de 50 ml, taça, garrafa).
- **Chope/barril:** o barril é um insumo em litros; cada produto-copo consome volume pela ficha. **Rendimento esperado** (copos por barril, descontando **perda de espuma/colarinho** configurável) contra o **volume realmente vendido**; ao **encerrar o barril**, o sistema registra a perda real e o desvio, e alerta quando passa do limite.
- **Troca de barril** como evento (abrir, encerrar, custo do barril, chopeira/torneira associada).
- **Dose de destilado:** a venda por dose baixa ml da garrafa. **Garrafa aberta** tem saldo em ml; ao esgotar, o sistema abre outra do estoque. A contagem de inventário inclui garrafas abertas (medidas por ml ou por nível).
- Relatórios: rendimento por barril, perda por chopeira/operador, custo por dose, desvio teórico × real de bebidas.
- Entra no **CMV teórico × real** (F10) para bebidas, que é onde ele mais pesa em bar.

### F18.5 Painel de exceções (antifraude simples)
Complementa a auditoria (F10) e as notificações (F13); **sem pontuação automática nem inteligência artificial no início**.
- **Notificar o administrador** de ações sensíveis, por regra configurável: desconto ou cortesia acima do limite, cancelamento de item após envio, estorno, fechamento forçado, caixa com diferença, reabertura de comanda, alteração de preço, login fora do horário.
- **Painel de exceções:** contagem e valor de cada tipo de ação sensível **por operador, caixa e período**, com comparação à média dos pares e ao período anterior, para destacar quem foge do padrão; clique abre a linha do tempo da comanda e o log.
- Marcação de revisão ("conferido", com comentário) por quem tem permissão.
- Tudo sobre dados que já são gravados na auditoria (nenhuma coleta nova).

### F18.6 Documentos e vencimentos
- **Cadastro de documentos** da organização e das unidades: alvará, bombeiros, vigilância sanitária, licenças, contratos, certificados, seguros, com **arquivo anexo**, número, **data de validade**, responsável e lembretes (ex.: 60, 30, 7 dias).
- **Manutenção de equipamentos:** cadastro (chopeira, freezer, coifa, ar-condicionado), plano de manutenção recorrente, histórico de serviços e custo (pode gerar conta a pagar).
- **Calendário (F15):** camada "documentos e manutenções"; **notificações** (F13) antes do vencimento; painel de pendências no dashboard.
- Arquivos em armazenamento privado com link temporário; acesso por permissão.

### F18.7 Rentabilidade por evento
- O **evento do calendário (F15)** ganha dados financeiros: couvert, consumação mínima, **cachê e custos previstos** (artista, som, segurança, divulgação).
- **Resultado do evento** = vendas atribuídas (da unidade, na janela de tempo do evento) − CMV − cachê − custos, com **previsto × real** e comparação com eventos parecidos e com o mesmo dia da semana sem evento.
- Ranking e histórico por tipo de evento e por artista/atração.

### F18.8 Compras: preços, orçamento e pedido ao fornecedor
- **Histórico de preço** por insumo e fornecedor (de compras e NF-e), **alerta de aumento** acima de um limite e **ranking** de melhor preço.
- **Orçamento por categoria de despesa**: previsto × realizado por mês, com alerta em 80% e 100%.
- **Sugestão de compra** mínima: insumos abaixo do mínimo agrupados pelo fornecedor preferido, com quantidade sugerida pelo consumo recente.
- **Pedido de compra ao fornecedor**: criado manualmente ou da sugestão, **gerado em PDF e enviado por e-mail** (WhatsApp depois); estados enviado, confirmado, recebido; ao receber, gera a compra (F3.15).

### Permissões
```
resumo_dia:receber            resumo_dia:configurar [A]
painel:ver_ao_vivo            painel:ver_consolidado
margem:ver   margem:configurar_alvo [A]   margem:simular   preco:aplicar_sugestao [A][L]
bebida:ver_rendimento   barril:trocar [A]   barril:encerrar [A]   garrafa:abrir
bebida:configurar_perdas [A]
excecao:ver_painel   excecao:marcar_revisado [A]   excecao:regras_configurar [A]
documento:ver   documento:gerenciar [A]   equipamento:ver   equipamento:gerenciar [A]
documento:baixar_arquivo
evento:ver_resultado   evento:editar_financeiro [A]
compra:ver_historico_preco   orcamento:ver   orcamento:gerenciar [A]
pedido_compra:criar   pedido_compra:enviar [A]
```

---

## Catálogo de features: status
Concluído em rascunho: F1 a F18 e requisitos não funcionais (`02b`). Cobertura do sistema atual conferida em `02c`. Próximo: `03-seguranca`, depois `04-arquitetura`, `05-ux-design`, `06-roadmap`.
