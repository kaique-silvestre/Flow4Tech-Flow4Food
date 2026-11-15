# Fase 2, Marco 2B: gestão na web e app para várias telas

Fatia: S-05
Aberta: 2026-11-15
Fechada: em andamento
Tickets: a registrar em `docs/tickets.md` quando cada um abrir (próximo livre ao abrir)

Status: rascunho 1 (2026-11-15), escrito pelo assistente a partir do Marco 2B da `S-04-salao-rapido/spec.md` (B1 a B9), de `docs/design/DESIGN.md` e de `01-visao.md` D17. **Não aprovado.** Itens *(proposta)* são decisões a confirmar pelo dono.

## Problema
O 2A entrega o salão só para celular (390x844 e 360x740). O DESIGN.md diz que acima de 480 px a coluna do celular fica centralizada e que a gestão na web "assume", mas nada disso existe. A visão (D17) promete celular, tablet e computador com **uma interface adaptável, sem versão separada**. A gestão do restaurante (central de comandas, promoções, clientes, estorno, agenda, reservas) é trabalho de computador e tablet, então não dá para construí-la antes de ter a base de várias telas.

## Solução
Dois blocos, nesta ordem:
1. **Bloco R: base para várias telas, pronta antes de qualquer tela de gestão.** O app inteiro passa a se adaptar a celular, tablet e computador. Sem isso, nenhum item do bloco G começa.
2. **Bloco G: gestão na web** (B1 a B9 do 2B), já nascendo adaptável.

Onde mora a gestão *(proposta)*: no `frontend/app`, rota `/gestao`. O `frontend/control` fica reservado para a Control Tower (Fase 6; domínio de identidade separado, ADR-005), não para o restaurante.

## Bloco R: base para várias telas (pré-requisito de tudo)
| # | Ticket | Tam. |
|---|---|---|
| R1 | **Decisão de layout por faixa** (ADR): faixas (celular até 599, tablet 600 a 1023, computador 1024 ou mais), o que muda em cada uma (navegação, colunas, densidade, alvos de toque 44 px no toque e menores com ponteiro), tokens de espaçamento e container no design system, DESIGN.md atualizado (substitui a frase "above 480px the phone column is centered") | M |
| R2 | **Shell adaptável** em `Shell.tsx` e no design system: barra de abas embaixo no celular, trilho lateral no tablet, barra lateral no computador; cabeçalho com empresa, unidade, turno e usuário (hoje só "Salão" e "Sair"); filtrada por permissão | M |
| R3 | **Salão em tablet**: lista e detalhe lado a lado (comandas e comanda), mapa de mesas maior, folhas (sheets) viram painel lateral ou diálogo; paridade de função com o celular, sem tela nova de regra | G |
| R4 | **Salão em computador**: mesmo conteúdo do tablet com teclado e atalhos básicos (busca, nova comanda), foco visível e navegação por teclado; sem perder uso no celular | M |
| R5 | **Testes e gates para as três faixas**: Playwright e capturas em 390x844, 360x740, 820x1180 (tablet) e 1440x900 (computador), claro e escuro; axe nas três faixas; revisão do `design-reviewer` por faixa; dependency-cruiser sem regra nova de pasta | M |

Critério de saída do R: **nenhuma tela do salão do 2A quebra, corta conteúdo ou perde função em 820 e 1440**, e as capturas aprovadas ficam em `docs/design/refs/`. Toda tela do bloco G nasce já passando o R5.

## Bloco G: gestão na web (Marco 2B)
Vem do 2B da S-04. Cada item é adaptável por construção (usa o shell do R2 e as faixas do R1).
| # | Ticket | Roadmap | Tam. |
|---|---|---|---|
| G1 | Notificações em tempo real (SSE) sobre os eventos do outbox, versão da comanda como fonte de ordem. Pré-requisito P3 da S-04: despachante do outbox e agendamento ligados | 1.9 | G |
| G2 | Central de comandas na web: lista e mapa ao vivo, filtros (garçom, mesa, tempo aberto, número), edição com permissão, fechamento forçado. Aproveita o R3 | 2.5 | M |
| G3 | Estorno de pagamento (total e parcial, motivo, volta ao turno certo, limite) e cancelamento de comanda com pagamentos | F4.5, F1.5 | M |
| G4 | Clientes mínimo e consentimento (LGPD): cadastro, busca, duplicidade, vínculo opcional à comanda, mascaramento sem `cliente:ver-dados-pessoais` | 2.9 | M |
| G5 | Fiado e vouchers (depende de G4) | F4.6, F4.7 | M |
| G6 | Promoções e combos: tela própria, pontual ou recorrente, horário, unidades, aplicação automática, promoção registrada no item | 2.4 | G |
| G7 | Agenda de funcionamento e regras de horário; o resolvedor do dia operacional passa a ler `dia-operacional.*` | 2.10b | M |
| G8 | Reservas pelo caminho da comanda (estado Reservada, "Chegou") | F1 | M |
| G9 | Edição das permissões de cada perfil pelo dono (hoje os perfis-modelo são só o ponto de partida) *(proposta, confirmar o escopo)* | 4.6 | M |
| G10 | Fechamento da Fase 2: medições com carga simulada, critério de saída, aprovação do cliente | saída da Fase 2 | M |

Ordem sugerida: **R1, R2, R3, R4, R5**, depois G1, G2 (usa R3), G7, G3, G4, G5, G6, G8, G9, G10. G1 e G2 podem andar em paralelo com R4 e R5 se houver mais de um executor.

## Critérios de aceite
1. O app inteiro (salão do 2A e telas de gestão) funciona em celular, tablet e computador, com paridade de função, sem versão separada.
2. Capturas aprovadas pelo `design-reviewer` nas quatro resoluções do R5, nos dois temas; axe sem violação nas três faixas.
3. Dois dispositivos na mesma comanda sem perda, e a central na web reflete a comanda do garçom em tempo real (G1, G2).
4. Estorno, clientes e fiado com isolamento entre organizações, auditoria e matriz de permissões testados.
5. Metas de desempenho medidas com carga simulada e aprovação do cliente (G10).
6. Todos os gates do CI verdes; cada ticket com revisão independente sem BLOCK.

## Fora de escopo
Control Tower e cobrança (Fase 6); estoque e ficha técnica (Fase 3); relatórios e dashboard (Fase 4); KDS, impressão por praça e Bluetooth (Fase 9); NFC-e e TEF/Pix automático (Fase 10); app nativo (a interface é web adaptável).

## Decisões do dono (2026-11-15)
- **Ordem:** o piloto em modo sombra (celular, testado pelo próprio dono) vem primeiro; o bloco R começa depois dele e antes de qualquer G.
- **Faixas:** uso comercial, sem aparelho de referência: celular, tablet a partir de 600 px e computador a partir de 1024 px.

## Perguntas abertas
- **Q2. Gestão no `app` ou em app próprio?** *(proposta: no `app`, rota `/gestao`)*
- **Q4. Teclado e atalhos no computador (R4):** só o básico ou um conjunto completo?
- **Q5. G9 (edição de permissões):** entra no 2B ou espera a Fase 4?
- **Q6. Identidade visual v6 ou v7** (herdada da Fase 1B): os tokens finais mudam as capturas do R5; decidir antes do R1.
