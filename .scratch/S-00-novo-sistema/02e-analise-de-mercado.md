# Novo sistema — Análise de sistemas semelhantes (lacunas de features)

Status: rascunho 1, **proposta para decisão**. Nada aqui entrou ainda em `02-features`; cada item espera seu aceite.

## Método e limites
- **Referências:** sistemas brasileiros de bar/restaurante (Saipos, Consumer, Colibri/Totvs Chef, Goomer, Zig, Teknisa, Grandchef) e internacionais (Toast, Square for Restaurants).
- **Fontes:** pesquisa pública em 2026-10-21 (páginas de fornecedores, guias e comparativos) e conhecimento geral do setor. As páginas de fornecedores são material comercial, não verificado de forma independente; para Goomer, Zig, Grandchef e Consumer a pesquisa **não trouxe listas de recursos confiáveis**, então esses nomes pesam pouco aqui. Itens marcados **(setor)** vêm do conhecimento geral de como o mercado funciona, não de uma fonte citada.
- **Uso:** achar o que um cliente de bar/restaurante **espera encontrar** e que ainda não está no catálogo. Não é obrigação de copiar concorrente.
- Classificação: **P1** = deveria estar na primeira versão ou entrar no modelo agora; **P2** = próxima fase, com encaixe previsto; **P3** = futuro, só registrar.

## 1. Lacunas encontradas

### Operação de salão e venda
| # | Feature | O que é | Origem | Prio |
|---|---|---|---|---|
| G1 | **Mapa de mesas com status** | Layout visual por área; mesa livre, ocupada, aguardando conta, reservada; tempo de ocupação; chamar garçom | Mercado: gestão de mesas | **P1** |
| G2 | **Tipos de venda** | Mesa, balcão, retirada e (depois) delivery, no mesmo modelo de comanda | Mercado: pedidos de balcão, mesa e delivery | **P1** (modelo) |
| G3 | **Consumação mínima e couvert artístico** | Regras por unidade/evento, aplicadas na conta | (setor, bares) | P2 |
| G4 | **Venda por peso** | Produto vendido por quilo com balança (self-service/buffet) | (setor) | P2 |
| G5 | **Cardápios por horário e canal** | Café, almoço, jantar, happy hour; cardápio de salão vs delivery; preços por canal | Mercado | P2 |
| G6 | **Meio a meio / produtos fracionados e modificadores avançados** | Combinar sabores/partes, preço pelo maior ou pela média | (setor, pizzarias; relevante conforme o cliente) | P3 |
| G7 | **Comanda por cartão, QR ou código de barras** | Identificar a comanda por leitura (cartão numerado, pulseira) | (setor, bares/baladas) | P2 |

### Relacionamento com o cliente
| # | Feature | O que é | Origem | Prio |
|---|---|---|---|---|
| G8 | **Cadastro de clientes (CRM básico)** | Cliente com contato, aniversário, histórico de consumo, **consentimento LGPD**; base do fiado, fidelidade, reservas | Mercado: banco de dados de clientes, frequência e preferências | **P1** (cadastro) |
| G9 | **Fidelidade e cashback** | Pontos, cashback, cupons, níveis | Mercado: programa de fidelidade e promoções | P2 |
| G10 | **Reservas e fila de espera** | Reservas com controle de sobreposição, fila, aviso ao cliente (SMS/WhatsApp), sinal/depósito | Mercado: fila por tablet e lembretes | P2. **Decisão (2026-10-28): sem WhatsApp nem SMS nesta versão**; aviso só por e-mail ou na tela |
| G11 | **Pesquisa de satisfação / NPS e avaliações** | Pergunta ao cliente após o consumo; painel de nota | (setor) | P3 |
| G12 | **Cashless / crédito pré-pago** | Recarga em cartão ou pulseira e consumo debitado (bar, balada) | (setor; ligado a "voucher") | P3 |

### Canais e integrações
| # | Feature | O que é | Origem | Prio |
|---|---|---|---|---|
| G13 | **Cardápio digital com QR e pedido na mesa** | Cliente escaneia, vê o cardápio, pede e paga (Pix) sem garçom | Mercado: cardápio digital | P2 |
| G14 | **Delivery e retirada** | Pedido, taxa de entrega, entregador, acerto, roteirização | Mercado: delivery e roteirização | P3 (modelo previsto em G2) |
| G15 | **Integração com marketplaces** (iFood e similares) | Receber pedidos no mesmo fluxo | Mercado: integração com apps de delivery | P3 |
| G16 | **Robô de WhatsApp para pedidos** | Atendimento e pedidos por WhatsApp | Mercado | P3 |
| G17 | **API pública e webhooks** | Chaves por organização, eventos para terceiros, documentação | (setor); coerente com D6 e A7 | P2 (desenho agora) |
| G18 | **Periféricos** | Impressora térmica, gaveta, leitor de código de barras, balança, pinpad/TEF, tela do cliente | Mercado; hoje só citados em F6/F7 | P2 |

### Estoque e compras
| # | Feature | O que é | Origem | Prio |
|---|---|---|---|---|
| G19 | **Sugestão de compra** | Calcula o que comprar a partir de consumo, estoque mínimo e ciclo do fornecedor | (setor) | P2 |
| G20 | **Cotação entre fornecedores** | Comparar preços e escolher o fornecedor | (setor) | P3 |
| G21 | **Ordem de produção / preparo em lote** | Produzir molhos, bases e pré-preparos, consumindo insumos e gerando insumo intermediário com custo | (setor; estende sub-receitas) | P2 |
| G22 | **CMV teórico vs real** | Compara o consumo pela ficha (teórico) com o consumo apurado pelo inventário (real); revela desperdício e furto | (setor; muito valorizado) | **P1** (no modelo do inventário) |
| G23 | **Lotes e validade** | Controle por lote, validade, PEPS | (já "decidir depois" no F3) | P2 |

### Financeiro, fiscal e contabilidade
| # | Feature | O que é | Origem | Prio |
|---|---|---|---|---|
| G24 | **Exportação para o contador** | XMLs fiscais, plano de contas, lançamentos em formatos usuais (CSV/OFX) | (setor) | P2 |
| G25 | **Conciliação bancária** | Importar extrato e casar com lançamentos | (setor; citado como "depois" no F8.4) | P2 |
| G26 | **Metas** | Metas de faturamento por unidade, garçom e período, com acompanhamento | (setor) | P2 |
| G27 | **Rateio da taxa de serviço** | Distribuir os 10% entre garçons e equipe por regra (hoje só comissão simples) | (setor, Brasil) | P2 (decisão do produto: manter comissão simples) |
| G28 | **Fiscal completo** | NFC-e/SAT, devolução, inutilização, contingência, SPED, nota de entrada | Mercado: cupom fiscal | Já previsto em F7 |

### Equipe
| # | Feature | O que é | Origem | Prio |
|---|---|---|---|---|
| G29 | **Escala e ponto** | Turnos, escala semanal, registro de ponto, banco de horas simples | Mercado (Toast: escalas e folha) | P2 |
| G30 | **Acerto de garçons e entregadores** | Fechamento de acerto com comissão e valores a receber | Mercado: acerto de entregadores e garçons | P2 (parte já em F9) |

### Inteligência, relatórios e adoção
| # | Feature | O que é | Origem | Prio |
|---|---|---|---|---|
| G31 | **Engenharia de cardápio** | Matriz popularidade × margem (estrelas, vacas, abacaxis, cães) para decidir preço e destaque | (setor) | **P1** (relatório sobre dados já existentes) |
| G32 | **Indicadores de ocupação e giro de mesa** | Taxa de ocupação, tempo médio de permanência, tempo de espera | Mercado: ocupação e tempo de espera | P2 |
| G33 | **Previsão e sugestões** | Previsão de vendas por dia/hora, sugestão de escala e de reposição | (setor) | P3 |
| G34 | **Importação em massa** | Planilhas (CSV/Excel) para produtos, insumos, fornecedores e clientes, com modelo de planilha e validação | (setor; essencial para adoção) | **P1** |
| G35 | **Onboarding guiado e modelos de cardápio** | Assistente, dados de exemplo, modelo de bar/restaurante já configurado | (setor) | P2 (F14.5 cobre o assistente) |
| G36 | **Central de ajuda e suporte no app** | Base de conhecimento, tour, chamado ao suporte com contexto (organização, tela) | (setor) | P2 |
| G37 | **Modo treinamento / ambiente de demonstração** | Operar sem afetar dados reais, para treinar equipe | (setor) | P3 |
| G38 | **Gift card / vale-presente** | Venda e uso de vale | (setor; ligado a voucher) | P3 |

## 2. Lacunas de requisito não funcional e de plataforma
| # | Item | Nota |
|---|---|---|
| N1 | **Acessibilidade e modo de alto contraste/escuro** para operação em ambiente com pouca luz | Bar à noite; entra no `05-ux-design` |
| N2 | **Impressão resiliente** (fila local de impressão, reimpressão, falha de impressora) | Parte de F6/F7 |
| N3 | **Atualização do app sem interromper o salão** | Parte do deploy sem parada (A10) |
| N4 | ~~Dispositivos de salão em quiosque (tela travada, auto-login por PIN)~~ | **Descartado** (dono removeu o PIN: só usuário/e-mail + senha, sessão do salão até o fim do turno) |

## 3. Recomendação
1. **Entram no catálogo agora (P1):** G1 mapa de mesas com status, G2 tipos de venda (mesa, balcão, retirada) no modelo, G8 cadastro de clientes com consentimento, G22 CMV teórico vs real no desenho do inventário, G31 engenharia de cardápio, G34 importação em massa.
2. **Entram no modelo, sem construir (P2/P3):** delivery e marketplaces (via tipo de venda e canal), reservas, fidelidade, cardápio digital, cashless, periféricos, API pública, lotes e validade. A arquitetura precisa **abrir o encaixe** (entidade cliente, tipo de venda, canal, eventos, adapters, API pública), sem codificar tela.
3. **Fica no roadmap como fases posteriores (P2):** escala e ponto, sugestão de compra, ordem de produção, conciliação bancária, exportação para o contador, metas, rateio da taxa de serviço (só se o produto abandonar a comissão simples).
4. **Registrar e não planejar agora (P3):** NPS, previsão, modo treinamento, gift card, robô de WhatsApp, cotação.

## 4. Decisões tomadas
- **G1 Mapa de mesas:** entra, como **visão derivada das comandas** (mesa é cadastro opcional; status calculado). Ver `02` F1.16.
- **G2 Tipos de venda:** **não é necessário agora.** Fica só como regra de modelo (a comanda não exige mesa e tem campo de canal reservado).
- **G8 Clientes:** entra como **base mínima** (F16), porque o fiado já exige; fidelidade e reservas ficam preparados.
- **G31 Engenharia de cardápio:** entra (relatório, F10).
- **G34 Importação em massa:** entra (F17), com modelo de planilha **e** mapeamento de colunas livre.
- **G22 CMV teórico × real:** entra junto do inventário e dos relatórios (F10); não houve objeção.

## 5. Decisões adicionais
- **Delivery, marketplaces (iFood) e robô de WhatsApp: fora de escopo**, sem previsão. Não considerar no desenho além da regra de modelo já registrada.

## 6. Decisões ainda abertas
1. Concorda com os **seis P1** acima como adição ao catálogo agora?
2. **Delivery e marketplaces:** são um objetivo próximo (6 a 12 meses) ou distante? Muda se o modelo de venda já nasce com canal e endereço de entrega.
3. **Cliente (CRM) desde o início:** o fiado, a fidelidade e as reservas dependem dele. Concorda em ter o cadastro de clientes no núcleo, com consentimento LGPD?
4. **Seus clientes atuais usam alguma coisa que não está aqui?** Se o restaurante atual tem pedido recorrente de alguma feature, ela entra como P1 ou P2.

## 7. Brainstorm: o que mais poderíamos ter (fora do que o mercado lista)

Ideias pensadas para **bar e restaurante pequeno que cresce**, ordenadas por valor para o dono. Esforço: B baixo, M médio, A alto. Nada daqui está aceito; é matéria-prima para decidir.

### A. Específicas de bar
| # | Ideia | O que resolve | Esforço |
|---|---|---|---|
| I1 | **Controle de chope e doses** | Barril com rendimento esperado × real, perda por espuma, venda por copo e por dose, garrafa aberta (dose de destilado com saldo da garrafa). Estende a ficha técnica e o estoque | M |
| I2 | **Garrafeira / reserva de garrafa** | Garrafa aberta guardada para um cliente, com saldo, validade e comanda | B |
| I3 | **Rentabilidade por evento** | Cruza o calendário (show, jogo, festa) com vendas, couvert, cachê e custo: resultado de cada evento | B |

### B. Para o dono acompanhar à distância
| # | Ideia | O que resolve | Esforço |
|---|---|---|---|
| I4 | **Resumo do dia automático** | Mensagem/e-mail ao fechar a operação: faturamento, ticket médio, CMV, caixa, alertas, comparação com o mesmo dia da semana passada | B |
| I5 | **"Como está a casa agora?"** | Painel ao vivo no celular: comandas abertas, faturamento da hora, estoque crítico, caixa; por unidade e consolidado | B |
| I6 | **Central antifraude** | Padrões suspeitos por operador (cancelamentos, descontos, estornos, cortesias, fechamento forçado, horários), pontuação de risco e revisão. Aproveita a auditoria que já existe | M |

### C. Dinheiro e margem
| # | Ideia | O que resolve | Esforço |
|---|---|---|---|
| I7 | **Alerta de margem e simulador de preço** | Insumo subiu de preço: avisa quais produtos perderam margem e sugere novo preço; simula "e se eu subir 5%?" com impacto esperado | M |
| I8 | **Histórico e ranking de preços de fornecedor** | Evolução do preço por insumo e fornecedor, alerta de aumento, melhor fornecedor por item | B |
| I9 | **Orçamento e metas por categoria de despesa** | Previsto × realizado por mês; alerta de estouro | M |
| I10 | **Pedido de compra enviado ao fornecedor** | Gera PDF/mensagem pronta (e-mail, depois WhatsApp) a partir da sugestão de compra | B |

### D. Rotina da casa e conformidade
| # | Ideia | O que resolve | Esforço |
|---|---|---|---|
| I11 | **Checklists e tarefas** | Abertura e fechamento da casa, limpeza, conferência de câmara fria; responsável, horário, foto, assinatura; alerta de não feito | M |
| I12 | **Controle sanitário** | Temperaturas de equipamentos, validade de manipulados, registros para fiscalização | M |
| I13 | **Documentos e vencimentos** | Alvará, bombeiros, vigilância sanitária, contratos, manutenção de equipamentos: arquivo com vencimento que aparece no calendário e notifica | B |
| I14 | **Manutenção de equipamentos** | Chopeira, freezer, coifa: agenda de manutenção e histórico | B |
| I15 | **Mural e recados por turno** | Avisos da gerência para a equipe, com confirmação de leitura | B |

### E. Cardápio e cliente
| # | Ideia | O que resolve | Esforço |
|---|---|---|---|
| I16 | **Alérgenos e informações do prato** | Alérgenos e restrições por produto (derivados dos insumos), foto, descrição; base do cardápio digital | B |
| I17 | **Receita com passo a passo** | Ficha técnica com modo de preparo e foto para padronizar e treinar | B |
| I18 | **Ranking e metas de garçons** | Vendas, ticket médio e metas por garçom, visível para a equipe, para motivar | B |

### F. Plataforma e integração
| # | Ideia | O que resolve | Esforço |
|---|---|---|---|
| I19 | **Open Finance / conciliação automática** | Extrato bancário e repasses de operadoras entram sozinhos e casam com lançamentos | A |
| I20 | **Exportação contábil direta** | Integração com sistemas contábeis comuns, além de arquivos | M |

## 8. Minha seleção (o que mais vale a pena)
1. **I4 Resumo do dia** e **I5 Painel ao vivo:** barato, usam dados que já teremos, e o dono sente valor todo dia.
2. **I7 Alerta de margem e simulador de preço:** liga estoque, compras e cardápio; é o que protege o lucro.
3. **I1 Chope e doses:** diferencial de bar; sem isso, o CMV de bebida fica impreciso.
4. **I6 Central antifraude:** a auditoria e as permissões já geram os dados; falta o painel.
5. **I13 Documentos e vencimentos:** barato e evita multa; encaixa no calendário.
6. **I3 Rentabilidade por evento:** barato, usa calendário e vendas.

As demais ficam como ideias registradas para fases futuras.

## 9. Decisões sobre as ideias (2026-10-21)
| Ideia | Decisão |
|---|---|
| I4 Resumo do dia, I5 Painel ao vivo | **Aceitas** (F18.1, F18.2) |
| I7 Alerta de margem e simulador | **Aceita** (F18.3) |
| I1 Chope, doses e garrafas | **Aceita** (F18.4) |
| I6 Central antifraude | **Aceita em versão simples**: notificação de ações sensíveis e painel de exceções por operador, sobre a auditoria existente; **sem pontuação automática** (F18.5) |
| I13 Documentos e vencimentos, I14 Manutenção de equipamentos | **Aceitas** (F18.6) |
| I3 Rentabilidade por evento | **Aceita** (F18.7) |
| I8, I9, I10 Compras (preços, orçamento, pedido em PDF) | **Aceitas** (F18.8), com sugestão de compra mínima |
| I11, I12, I15 Rotina da casa (checklists, sanitário, mural) | **Recusadas** |
| I2 Garrafeira | **Recusada** |
| I16, I17, I18 Alérgenos, receita passo a passo, ranking de garçons | **Recusadas** |
| I19 Open Finance (conciliação automática) | **Fora de escopo** (decisão: não mexer com isso). Nenhuma conciliação bancária automática; o `02` F8.4 passa a "importação de extrato por arquivo" como item opcional de fase tardia. |
| I20 Exportação contábil | **Pacote do contador** (arquivos) mantido como P2; integração direta com sistema contábil fora de escopo. |
