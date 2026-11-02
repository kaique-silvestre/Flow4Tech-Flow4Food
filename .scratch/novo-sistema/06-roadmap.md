# Novo sistema — Roadmap

Status: rascunho 2, **reordenado** após a decisão D16 (o lançamento rápido pelo celular é a primeira entrega utilizável) e a escolha do caminho B (priorizar no sistema novo, sem paliativo no sistema atual). Ordem e dependências, **sem datas** (prazo não restringe o plano, D9). Tamanhos relativos: **P** pequeno, **M** médio, **G** grande.

## R1. Princípios de ordenação

1. **Lançamento rápido primeiro.** O que o cliente mais precisa (D16) é a primeira coisa a ficar de pé e a ser testada por garçons reais.
2. **Fatia vertical fina antes de módulos inteiros.** Provar a arquitetura ponta a ponta com o fluxo mínimo: entrar → abrir comanda → lançar item → pagar → fechar caixa, já na tela mobile do garçom.
3. **Segurança e isolamento são fundação.** Isolamento por organização, motor de permissões, auditoria e eventos nascem na primeira fase.
4. **Risco antes de volume.** Spikes e o motor de permissões antes das telas.
5. **Nunca dois sistemas com dados oficiais ao mesmo tempo.** O sistema novo só vira a fonte de verdade quando tem **paridade mínima** com o que o restaurante usa no dia a dia. Antes disso, o que se faz com garçons reais é **piloto em modo sombra** (dados de teste ou espelho, sem substituir o oficial).
6. **Cada fase termina utilizável** e com critérios de saída verificáveis.
7. **Sem big bang.** O sistema antigo segue em produção até a virada, com plano de volta.
8. **Cada entrega carrega seus testes** (RNF7): domínio, integração com banco real, contrato de API, isolamento entre organizações e matriz de permissões.

## R2. Visão geral

| Fase | Nome | Resultado ao final | Tam. |
|---|---|---|---|
| 0 | Fundações e validações | Repositório, CI, ambientes, spikes e **protótipo do lançamento rápido testado com o cliente** | M |
| 1 | Plataforma base e fatia vertical | Organização, identidade, permissões, auditoria, eventos; fluxo mínimo de comanda **na tela mobile** | G |
| 2 | **Salão rápido** | App do garçom (PWA) completo, comandas, pagamentos, caixa, mesas; **piloto em modo sombra** com garçons reais | G |
| 3 | Estoque e compras | Insumos, fichas, baixa automática, compras, NF-e, inventário, alertas | G |
| 4 | Paridade mínima | Tudo que o restaurante usa hoje e ainda falta: relatórios nominais, dashboard, contas a pagar, comissões, calendário base, importação | G |
| 5 | **Piloto e virada** | Restaurante atual migrado, sistema novo como fonte de verdade; portão de segurança | M |
| 6 | Control Tower completa e cobrança | Assinaturas, planos, suporte, impersonação; **antes do segundo cliente** | M |
| 7 | Financeiro e relatórios completos | Contas a receber, DRE, fluxo, retenção por camadas, calendário completo | G |
| 8 | Gestão avançada | Margem, chope e doses, exceções, documentos, eventos, compras avançadas | G |
| 9 | Cozinha, KDS e impressão por praça | Fluxo de preparo | M |
| 10 | Fiscal e integrações de pagamento | NFC-e; TEF/Pix automático | G |

A **Fase 6 pode correr em paralelo** com as 7 e 8. As fases 9 e 10 podem ser antecipadas conforme a necessidade do cliente.

**Por que a virada (5) vem logo após a paridade mínima (4):** o restaurante não precisa esperar por financeiro completo nem por gestão avançada para ganhar o lançamento rápido. As fases 6 a 10 chegam já **em produção**, em cima do sistema novo.

---

## R3. Fase 0: Fundações e validações

| # | Entrega | Detalhe |
|---|---|---|
| 0.1 | Repositório e estrutura | Solução .NET modular, front React/TypeScript, convenções, instruções para agentes (novos `AGENTS.md`/`CLAUDE.md`) |
| 0.2 | CI/CD e ambientes | Portões obrigatórios; ambientes `staging` e `production` no Railway; front na Vercel |
| 0.3 | ~~Spike de latência~~ | **Validado pelo dono (2026-10-31):** latência do salão até o Railway (Virgínia) atestada. Só reconfirmar com a carga real da Fase 2 (`07` T7) |
| 0.4 | Spike do worker | Worker dentro da API; segundo serviço no Railway sem domínio público |
| 0.5 | Spike do motor de autorização | Cedar contra três casos reais; decidir usar ou construir |
| 0.6 | Spike de dados e fila | Dapper/EF Core e Wolverine/Hangfire/Quartz; RLS com contexto por transação |
| 0.7 | Backup | Backup lógico diário fora do Railway e teste de restauração |
| 0.8 | Plano da Vercel | Confirmar plano compatível com uso comercial |
| 0.9 | Direção visual (`05-ux-design`) | Linguagem visual, componentes base, navegação |
| 0.10 | **Protótipo do lançamento rápido** | Tela mobile navegável (sem back-end real), testada com o cliente e garçons em **aparelho real**: meta de 2 toques por item e abertura de comanda em menos de 10 s; decide PWA ou nativo (`04` A11) |
| 0.11 | ADRs | Escrever e aprovar as decisões do `04` |

**Critério de saída:** decisões dos spikes em ADR; pipeline verde; `staging` no ar; **protótipo aprovado pelo cliente**; latência validada e meta confirmada.

---

## R4. Fase 1: Plataforma base e fatia vertical

| # | Entrega | Origem | Tam. |
|---|---|---|---|
| 1.1 | Organização e unidades, contexto de organização em toda requisição | D3, S5 | M |
| 1.2 | Banco com RLS, usuário de aplicação sem bypass, **testes de isolamento entre organizações** | S5 | M |
| 1.3 | Identidade: e-mail/senha, Google, conta operacional (usuário + senha), troca obrigatória, recuperação | F11, S6 | G |
| 1.4 | Sessões e tokens: acesso curto, refresh rotativo, revogação, sessões | S7 | M |
| 1.5 | ~~Dispositivos do salão e PIN~~ | removido: só usuário/e-mail + senha | — |
| 1.6 | Motor de permissões: catálogo, perfis padrão e personalizados, escopo por unidade, limites, deny, simulador; **matriz testada** | F11, S8, `02d` | G |
| 1.7 | Auditoria imutável encadeada e log técnico com correlação | S14 | M |
| 1.8 | Eventos de domínio: outbox, despachante, fila em PostgreSQL | A5 | M |
| 1.9 | Notificações núcleo: central in-app e tempo real por SSE | F13 | M |
| 1.10 | Configurações em camadas | F14 | M |
| 1.11 | Proteção de login e alertas de segurança | S12 | P |
| 1.12 | **Fatia vertical mobile:** na rota `/salao`, entrar, abrir comanda, lançar item, pagar em dinheiro, fechar, abrir/fechar caixa, com permissão, auditoria e eventos | F1, F4, F5 | M |
| 1.13 | Control Tower mínima: super admin, login separado, restrição por IP, criar organização e unidades | F12, D12 | M |
| 1.14 | Observabilidade: logs estruturados, métricas, Sentry | RNF6 | P |

**Critério de saída:** fatia vertical operando em `staging` no celular, com dois tenants de teste sem vazamento; isolamento e matriz de permissões verdes no CI; auditoria verificável; latência confirmada em rota real.

---

## R5. Fase 2: Salão rápido (primeira entrega ao cliente)

Ordem **dentro** da fase: lançamento rápido primeiro, o resto depois.

| # | Entrega | Origem | Tam. |
|---|---|---|---|
| 2.1 | **App do garçom (PWA) de lançamento rápido** (D16): favoritos e mais vendidos, repetir a última rodada, busca e código de barras, teclado numérico, adicionais em um toque, enviar rodada de uma vez, **fila de ações pendentes visível**, reconexão sem perder lançamento | F1, RNF2, RNF8 | G |
| 2.2 | Cardápio: produtos, categorias (2 níveis), preço, preço promocional, ativo/inativo, disponibilidade | F2.1, F2.2, F2.10, F2.11 | M |
| 2.3 | Opções e adicionais; combos | F2.3, F2.5 | M |
| 2.4 | Tela Promoções (promoções e combos): pontual/recorrente, horário, unidades, aplicação automática | F2.4, F2.5, F2.12 | M |
| 2.5 | **Central de comandas na web**: lista e mapa em tempo real, filtros por garçom, mesa e tempo aberto, edição com permissão | F1, F18.2 base | M |
| 2.6 | Mesas, áreas e mapa derivado das comandas | F1.16 | M |
| 2.7 | Comandas completas: alterar/cancelar item (motivos), cortesia, brinde, desconto com limite, taxa de serviço, transferir, juntar, dividir, observações, cronômetro, histórico, versão (concorrência) | F1 | G |
| 2.8 | Pagamentos: métodos cadastráveis, parcial, dividido (6 formas), troco, gorjeta, estorno, fiado | F4 | G |
| 2.9 | Clientes (base mínima) e consentimento | F16 | M |
| 2.10 | Caixa completo: janela, sangria, suprimento, conferência cega, diferença e aprovação | F5 | M |
| 2.10b | Agenda de funcionamento: horário por dia da semana, exceções por data, comportamento fora do horário | F5.8, F5.9 | M |
| 2.11 | Comprovantes e pré-conta | F7 agora | M |
| 2.12 | **Importação do cardápio** por planilha (primeiro uso de F17, para o piloto) | F17 | M |

**Marco 2A: piloto em modo sombra.** Com o cardápio real importado, garçons usam o app novo em uma ou mais noites de teste. Os dados ali **não são oficiais** (o sistema antigo continua a fonte de verdade). O objetivo é medir velocidade, achar atritos e ajustar o fluxo antes de qualquer migração.

**Critério de saída:** metas de toques e de tempo atingidas em uso real; dois dispositivos na mesma comanda sem perda; divisão de conta validada por testes de propriedade; metas de desempenho; aprovação do cliente sobre o fluxo.

---

## R6. Fase 3: Estoque e compras

| # | Entrega | Origem | Tam. |
|---|---|---|---|
| 3.1 | Insumos (unidades, conversões, EAN, embalagem), fornecedores | F3.1, F3.2, F3.13 | M |
| 3.2 | Movimentações imutáveis, saldo materializado, reserva e baixa no lançamento, política de estoque negativo | F3.5, F3.12 | G |
| 3.3 | Ficha técnica (opcional): rendimento, sub-receitas, ficha congelada, produção possível, custo | F2.6, F2.7, F2.9 | G |
| 3.4 | Alertas de estoque por evento com antirruído | F3.8, F13 | M |
| 3.5 | Compras: tipos, receber, cancelar, estorno | F3.3, F3.14, F3.15 | M |
| 3.6 | Importação de NF-e com conciliação e validação segura | F3.16 | G |
| 3.7 | Inventário/contagem, ajustes com aprovação, perdas, consumo interno | F3.6, F3.7, F3.11 | M |
| 3.8 | Transferência entre unidades | F3.10 | P |
| 3.9 | Disponibilidade automática do cardápio pelo estoque | F2.8 | P |
| 3.10 | Importação de insumos, fornecedores e saldos iniciais por planilha | F17 | M |

**Critério de saída:** saldo sempre igual à soma das movimentações (verificação automática); testes de concorrência na reserva; NF-e real importada de ponta a ponta; alertas em até 5 s.

---

## R7. Fase 4: Paridade mínima (o que falta para substituir o sistema atual)

Nada novo de valor: é **reproduzir o que o restaurante usa hoje**, conforme `02c`, para poder desligar o sistema antigo.

| # | Entrega | Origem | Tam. |
|---|---|---|---|
| 4.1 | Relatórios nominais do sistema atual: vendas do dia, histórico de comandas, fechamento de caixa, DRE básico, CMV por produto, perdas e cortesias, vendas por garçom, produtos mais vendidos, pico por horário, vendas por produto | F10, `02c` | G |
| 4.2 | Dashboard com os widgets do sistema atual e pendências por permissão | F10 | M |
| 4.3 | Contas a pagar (lista, parcelas, pagar, a partir de compras) | F8.1, F8.2 | M |
| 4.4 | Equipe e comissões (regra simples), pagamento | F9 | M |
| 4.5 | Calendário base (eventos e tarefas, recorrência, camadas de pagamentos e entregas) | F15 | M |
| 4.6 | Gestão de usuários e perfis completa; perfis padrão | F11, `02d` | M |
| 4.7 | Importação de clientes, comissões em aberto e saldos pendentes | F17 | P |
| 4.8 | Resumo do dia e painel ao vivo (barato e visível) | F18.1, F18.2 | M |

**Critério de saída:** matriz de cobertura `02c` marcada como entregue e conferida **contra dados reais importados** (mesmos números dos relatórios do sistema antigo para o mesmo período).

---

## R8. Fase 5: Piloto e virada

| # | Entrega | Detalhe |
|---|---|---|
| 5.1 | **Migração do restaurante atual** | Plano em `08`: extrator do banco antigo gera as planilhas F17; ensaios repetidos em `staging` sobre legado anonimizado; conferência de cadastros, saldos de estoque e saldos em aberto; histórico conforme a opção escolhida pelo dono (`08` M3). Depende das respostas de `08` M8 |
| 5.2 | Operação em paralelo curta | Sistema novo em uso real, antigo disponível para conferência |
| 5.3 | **Portão de segurança** (`03` S18) | Isolamento 100% coberto, matriz de permissões, auditoria verificável, segredos, backup restaurado dentro do objetivo, alertas ativos, teste de invasão externo, plano de incidentes, termos e política de privacidade |
| 5.4 | Teste de carga | Meta de escala do RNF1 simulada |
| 5.5 | Ensaio de migração de nuvem | Subir o sistema em outra nuvem a partir de backup |
| 5.6 | Plano de virada e **reversão** | Janela, passos, verificação, volta ao antigo |
| 5.7 | Treinamento e material de ajuda | Roteiros por perfil |

**Critério de saída:** restaurante operando no sistema novo como fonte de verdade por um período combinado, sem retorno ao antigo; sistema antigo desligado com dados preservados em backup.

---

## R9. Fase 6: Control Tower completa e cobrança (antes do segundo cliente)

| # | Entrega | Origem | Tam. |
|---|---|---|---|
| 6.1 | Gestão de organizações, usuários e perfis de cada uma | F12.2, F12.13 | M |
| 6.2 | Planos e limites; criar unidades em nome da organização | F12.3, F12.12, F12.17 | M |
| 6.3 | Ciclo da assinatura (teste personalizável, ativa com tolerância, suspensa, bloqueada, encerrada) e telas informativas | F12.11 | G |
| 6.4 | Feature flags, comunicados com segmentação | F12.4, F12.5, F12.16 | P |
| 6.5 | Administradores delegados e política de impersonação por pessoa; impersonação com atribuição dupla | F12.6, F12.8, D15 | M |
| 6.6 | Cockpit, configurações globais, auditoria da plataforma, painel de operação, botões de emergência | F12.1, F12.7, F12.10, F12.14, F12.15, S16 | M |

**Critério de saída:** todo estado de assinatura bloqueia e libera conforme a tabela, decidido no servidor; impersonação testada; botões de emergência exercitados.

---

## R10. Fase 7: Financeiro e relatórios completos

Contas a receber, contas financeiras, fluxo de caixa, DRE gerencial completo, categorias e centros de custo (F8.3 a F8.9); calendário completo com notificações e iCal (F15); engenharia de cardápio e CMV teórico × real (F10); retenção por camadas, agregados e arquivamento frio (RNF1); exportação para o contador (pacote de arquivos); importação e exportação em massa completa (F17).

**Critério de saída:** relatórios longos dentro da meta; arquivamento testado com restauração da camada fria.

## R11. Fase 8: Gestão avançada

Alerta de margem e simulador (F18.3); chope, doses e garrafas (F18.4); painel de exceções (F18.5, adiado: a tela Auditoria já filtra "só sensíveis"); documentos e vencimentos (F18.6, adiado: fora do menu, vencimentos como lembrete no calendário); rentabilidade por evento (F18.7); compras avançadas (F18.8). **Assistente de IA com chave própria (F19)**: etapa 1 (leitura e navegação) já útil nesta fase; etapas 2 e 3 (ações com confirmação, ações amplas) em seguida. **Critério:** cada item validado com um mês de operação real.

## R12. Fases 9 e 10: Cozinha e fiscal

**Fase 9:** praças, pedido de preparo por evento, impressoras, tela de cozinha, ciclo do item (F6). **Fase 10:** documento fiscal, NFC-e por provedor, TEF e Pix automático por adapter (F7, F4). Ambas têm encaixe no modelo desde a Fase 1; a ordem relativa depende do cliente.

---

## R13. Rastreabilidade: de onde cada feature entra

| Feature | Fase |
|---|---|
| F1 Comandas | 1 (mínimo), 2 (completo) |
| F2 Cardápio | 2, 3 (ficha), 8 (margem) |
| F3 Estoque e compras | 3 (8 para chope e compras avançadas) |
| F4 Pagamentos | 1 (dinheiro), 2 (completo), 10 (TEF/Pix) |
| F5 Caixa | 1 (mínimo), 2 |
| F6 Cozinha/KDS | 9 |
| F7 Comprovantes e fiscal | 2 (comprovante), 10 (fiscal) |
| F8 Financeiro | 4 (contas a pagar), 7 (completo) |
| F9 Equipe e comissões | 4 |
| F10 Relatórios, dashboard, auditoria | 1 (auditoria), 4 (nominais), 7 (avançados) |
| F11 Identidade e acesso | 1, 4 (gestão completa) |
| F12 Control Tower | 1 (mínima), 6 |
| F13 Notificações | 1 (núcleo), 3 (estoque), 8 |
| F14 Configurações | 1 |
| F15 Calendário | 4 (base), 7 (completo) |
| F16 Clientes | 2 |
| F17 Importação/exportação | 2 (cardápio), 3 (insumos), 4, 5 (migração), 7 (completo) |
| F18 Gestão avançada | 4 (resumo e painel), 8 |
| F19 Assistente de IA (chave própria) | 8 (leitura e navegação, depois ações) |

Toda linha de `02c` está em alguma fase; o critério de saída da Fase 4 exige a conferência com dados reais.

---

## R14. Riscos do plano e mitigações

| Risco | Mitigação |
|---|---|
| O app do garçom é o coração da operação e a PWA pode não bastar | Protótipo na Fase 0 em aparelho real; decisão por critérios (`04` A11); a API não muda |
| O cliente espera por muito tempo sem ver nada | Protótipo na Fase 0 e piloto em modo sombra na Fase 2 dão resultado visível cedo |
| Piloto sombra vira "segundo sistema" informal | Regra: dados do piloto não são oficiais; encerrar o piloto com descarte combinado |
| Paridade mínima (Fase 4) subestimada | Matriz `02c` como lista de verificação; conferir números contra o sistema antigo |
| Fase 1 grande sem tela útil | A fatia vertical (1.12) roda no celular cedo; demonstrações a cada marco |
| Motor de permissões mais complexo que o previsto | Spike na Fase 0; começar com menos condições e ampliar |
| Escopo cresce | Congelar o escopo de cada fase ao iniciá-la; ideias novas entram na lista do `02e` |
| Latência do Railway pior em campo | Validada na Fase 0 (0.3); reconfirmar sob carga real na Fase 2 e manter gatilhos de migração (`04` A10) |
| Dados do legado inconsistentes | Importação com validação linha a linha e conferência manual na Fase 5 |

## R15. Como medir progresso
- Marcos por fase, com critério de saída verificável.
- Matriz de cobertura `02c`: cada linha sai de "requisitada" para "entregue e testada".
- Indicadores de qualidade: cobertura de domínio sensível, tempo do pipeline, falhas de isolamento (meta: zero), dívida técnica registrada.
- **Indicadores de produto do lançamento rápido:** toques por item, segundos para abrir comanda e lançar o primeiro item, itens por minuto, falhas de envio.

## R16. Decisões

**Fechadas:** caminho B (priorizar no sistema novo); virada logo após a paridade mínima; Control Tower completa depois da virada e antes do segundo cliente.

**Em aberto:**
1. **Cozinha (9) e fiscal (10)** podem ficar depois da virada, ou o restaurante atual precisa de algum antes?
2. O restaurante atual usa **clientes** e **mesas**? Define o que a Fase 2 precisa trazer antes do piloto. **Fiado:** resposta do dono (2026-10-22): "pode haver a opção de fiado". Tratar como recurso **opcional, ligado por organização/unidade** (D17); fica na Fase 2 (2.8 + 2.9) mas fora do caminho crítico do piloto.
3. Posso seguir com o `05-ux-design`? O lançamento rápido é onde o design decide o sucesso.
