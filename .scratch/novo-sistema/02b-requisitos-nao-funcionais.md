# Novo sistema — Requisitos não funcionais

Status: rascunho 1. Valores marcados **(proposto)** são metas sugeridas para confirmação; os demais vêm de decisões já tomadas.

## RNF1. Escala e capacidade

Premissa: hoje há 1 organização e 1 usuário, mas o sistema nasce pronto para crescer (D2, D3).

| Item | Meta de projeto (proposto) |
|---|---|
| Organizações | 1.000 |
| Unidades | 5.000 |
| Usuários cadastrados | 50.000 |
| Usuários simultâneos no pico | 5.000 |
| Comandas por unidade/dia | até 1.500 (casa grande em dia de pico) |
| Eventos de domínio/dia (todas as orgs) | ~5 milhões, ~60/s em média, picos de 10x |
| Histórico retido | camadas quente/agregada/fria com retenção por categoria (ver ciclo de vida dos dados) |

- A arquitetura deve **escalar horizontalmente** a camada de aplicação e permitir **separar leitura de escrita** (réplica de leitura) sem reescrita.
- Crescimento de dados: tabelas de eventos/movimentações **particionadas por tempo e por organização**; relatórios pesados em estruturas pré-agregadas.
- Validar com **teste de carga** antes de aceitar clientes além da meta; teste de carga é parte do pipeline, não evento único.
- **Isolamento de vizinho barulhento**: nenhuma organização degrada as outras (limites por organização, filas justas, rate limit).

### Ciclo de vida dos dados (retenção, arquivamento e exclusão)
Decisão: dados não ficam para sempre no banco principal. Em vez de apagar tudo após um prazo, o dado **desce por camadas**, e a exclusão definitiva só vale para o que a lei permite.

| Camada | O que fica | Onde | Prazo (proposto) |
|---|---|---|---|
| **Quente** | Dados operacionais completos (comandas, itens, pagamentos, movimentos, notificações) | Banco principal, partições mensais | últimos 12 a 24 meses |
| **Agregada** | Resumos por dia/unidade/produto/insumo/método (faturamento, itens vendidos, CMV, entradas e saídas) | Banco principal (tabelas pequenas) | indefinida (custo desprezível) |
| **Fria** | Dados completos antigos, compactados e criptografados | Armazenamento de objetos (barato), consultável sob demanda | até o fim da retenção legal (ex.: 5 anos para fiscal/financeiro) |
| **Excluída** | Tudo que passou da retenção legal e não tem obrigação de guarda | — | exclusão definitiva automática |

**Como funciona**
1. As tabelas grandes são **particionadas por mês**. Fechar um mês é barato: não se apaga linha a linha.
2. Rotina mensal: gera/atualiza os **agregados** do período, **exporta** a partição antiga para a camada fria (arquivo compactado e criptografado, com índice e verificação de integridade) e só então **remove a partição** do banco principal. Remover uma partição é instantâneo e não deixa lixo nem exige limpeza pesada.
3. Os **relatórios** usam os agregados para períodos longos e leem a camada fria só para detalhe antigo (resposta assíncrona: "estamos preparando, avisamos quando pronto").
4. **Antes de arquivar, a organização é avisada** e pode exportar. Exportação completa disponível a qualquer momento.
5. **Prazo por categoria**, não um só para tudo: financeiro/fiscal (obrigação legal, mais longo), vendas operacionais, auditoria, notificações e logs técnicos (curtos, ex.: 30 a 90 dias), anexos.
6. **Plano define o prazo da camada quente e fria** (ex.: plano básico 12 meses, plano maior 24 meses). Quem quiser mais paga.
7. **LGPD**: dados pessoais de clientes (fiado, fiscal) são anonimizados ao fim da finalidade, mantendo o valor contábil sem identificar a pessoa.
8. **Auditoria**: log de segurança com retenção própria e mais longa; cópia fria imutável.
9. Cada exclusão definitiva é registrada (o quê, quando, regra aplicada) e não é reversível; por isso a camada fria vem antes.

**Por que não apagar direto:** relatório de ano contra ano, fiscalização, disputa com cliente ou funcionário e prova de auditoria exigem o dado antigo. Apagar de vez economiza centavos de armazenamento e custa caro quando faz falta. A camada fria resolve o custo (armazenamento de objetos é muito mais barato que banco).

**Custo na prática:** uma venda ocupa em torno de 1 a 2 KB com itens e pagamentos. Uma casa grande com 1.500 comandas por dia gera ~1 GB por ano; mil organizações, ~1 TB por ano. Na camada quente isso exige disciplina; na fria custa pouco.

## RNF2. Desempenho

| Operação | Meta p95 (proposto) |
|---|---|
| Lançar item em comanda | < 300 ms (a **confirmar** com medição real no Railway/EUA Leste; pode ser relaxada na fase inicial) |
| Abrir comanda / registrar pagamento | < 500 ms |
| Telas operacionais (lista de comandas, caixa) | carregamento < 1,5 s em conexão 4G |
| Relatório do dia | < 3 s |
| Relatório de período longo (12 meses) | < 10 s, ou geração assíncrona com aviso |
| Alerta de estoque (do evento ao navegador) | < 5 s |

- Operação de salão tem prioridade sobre relatório: consultas pesadas **nunca** disputam recurso com o lançamento de itens.
- Interface responsiva em celular e tablet (garçom), com tela útil em redes instáveis (reconexão automática, ações pendentes visíveis). Offline completo continua fora de escopo (D7).

## RNF3. Disponibilidade e continuidade

| Item | Meta (proposto) |
|---|---|
| Disponibilidade mensal | 99,9% (≈ 43 min/mês), medida em horário operacional dos clientes |
| Janela de manutenção | fora do horário de pico, avisada por comunicado; deploys sem indisponibilidade |
| **RPO** (perda máxima de dados) | ≤ 5 min como meta; **fase inicial no Railway: até 24 h** (risco aceito, ver `04` A10) |
| **RTO** (tempo para restaurar) | ≤ 1 h |
| Backups | contínuos (restauração a ponto no tempo) + cópia diária em outra região |
| Teste de restauração | mensal e documentado |

- Degradação controlada: se um componente secundário cair (notificações, relatórios, integrações fiscais/TEF), **a operação de salão continua**.
- Páginas de status e histórico de incidentes para clientes.
- Todo efeito externo (e-mail, fiscal, pagamento) é **assíncrono, idempotente e com nova tentativa**.

## RNF4. Segurança (resumo; detalhe em `03-seguranca`)

- Isolamento rigoroso entre organizações **em todas as camadas**, inclusive no banco de dados, não só na aplicação.
- Autorização verificada **no servidor** em toda operação; nenhuma regra de segurança depende do front.
- Criptografia em trânsito (TLS) e em repouso; segredos fora do código, rotacionáveis.
- Dados sensíveis (documentos de clientes, credenciais de integração, certificado fiscal) com criptografia adicional por campo.
- Auditoria imutável (F10). Defesa em profundidade, limitação de taxa, proteção contra abuso, dependências verificadas e atualizadas.
- Registrado como risco aceito: sem 2FA por ora (ver `01-visao`).

## RNF5. Privacidade e LGPD

**Papéis:** a organização cliente é a **controladora** dos dados dos seus clientes e funcionários; a plataforma é **operadora**. Para os dados de usuários e cobrança da própria plataforma, somos controladores.

- **Minimização**: coletar o necessário. Cliente de bar normalmente não precisa de cadastro; fiado e fiscal exigem dados específicos, só nesses fluxos.
- **Base legal documentada** por tipo de dado (execução de contrato, obrigação legal fiscal, legítimo interesse, consentimento).
- **Direitos do titular**: acesso, correção, exportação e eliminação/anonimização, com fluxo operacional e prazo.
- **Eliminação vs retenção**: dados fiscais e financeiros têm retenção legal (anos); o sistema separa **o que precisa ficar** do que pode ser anonimizado, sem quebrar integridade do histórico (anonimizar a pessoa, manter o registro contábil).
- **Retenção por categoria** definida e aplicada por rotina automática; log de auditoria com retenção própria.
- **Acesso a dados pessoais é auditado**, inclusive da plataforma (impersonação, F12).
- **Residência dos dados**: a LGPD não exige hospedagem no Brasil, mas exige salvaguardas em transferência internacional. **Fase inicial no Railway fora do Brasil** (EUA Leste): contrato com o provedor, criptografia, política de privacidade informando a transferência. Hospedagem no Brasil entra com a migração de nuvem (`04` A10).
- **Incidentes**: procedimento de resposta e notificação (controlador e ANPD no prazo legal).
- **Contrato**: termos de uso, política de privacidade e acordo de tratamento de dados com cada organização.
- **Sem dado pessoal em logs técnicos, URLs e mensagens de e-mail/WhatsApp.**

## RNF6. Observabilidade e operação

- Logs estruturados com **id de correlação** por requisição, sem dados sensíveis.
- Métricas de negócio e técnicas (latência, erros, filas, uso por organização) e **alertas** acionáveis.
- Rastreamento distribuído nas operações críticas (lançar item, pagar, fechar).
- Painel de operação na Control Tower (F12.10).
- Feature flags e releases graduais; **reversão rápida** de qualquer deploy.
- Ambientes: desenvolvimento, homologação e produção, com dados de teste sintéticos (nunca dados reais fora de produção).

## RNF7. Qualidade e capacidade de evoluir (D6)

- **Pirâmide de testes**: unitários nas regras de domínio, integração por módulo (com banco real), contratos de API, ponta a ponta nos fluxos críticos (comanda → pagamento → fechamento → caixa), testes de segurança (isolamento de organização, permissões) e testes de carga.
- **Regras de negócio puras e testáveis**, isoladas de framework e banco.
- **Portões obrigatórios no CI**: testes, lint, tipos, verificação de dependências e de segredos; sem merge com falha.
- **Fronteiras de módulo explícitas**: um módulo só fala com outro por interface pública; dependências proibidas verificadas automaticamente.
- **Testes de isolamento de organização** automáticos: toda rota e consulta nova é testada contra vazamento entre organizações.
- **Migrações de banco** reversíveis, testadas e compatíveis com deploy sem parada.
- Documentação viva: ADRs para decisões, catálogo de features e permissões gerado a partir do código.
- Cobertura é meta de apoio, não de vaidade: **100% das regras de domínio sensíveis** (dinheiro, estoque, permissões) cobertas por testes.

## RNF8. Usabilidade, acessibilidade e localização

- Idioma inicial: **pt-BR**; textos externalizados desde o início (i18n pronto, outros idiomas depois).
- Moeda em BRL, com modelo de valores monetários seguro (inteiros em centavos, nunca ponto flutuante); preparado para outras moedas.
- **Fuso horário por unidade** e dia operacional (F5).
- Acessibilidade conforme WCAG 2.1 AA nas telas principais; operação por toque, teclado e leitor de código de barras.
- Navegadores suportados: versões recentes de Chrome, Edge, Safari e Firefox; Android e iOS atuais.
- **Dispositivos (D17):** celular (Android e iPhone, próprio ou do restaurante), tablet e computador; interface adaptável sem versão separada; desempenho aceitável em aparelho de entrada (orçamento de tamanho de pacote e de processamento no CI).
- **Temas claro e escuro (D17):** ambos desde o início, com troca manual e automática pelo sistema; contraste AA nos dois; tokens de design em vez de cores fixas.
- Tempo de aprendizado: um garçom novo deve lançar um pedido sem treinamento extenso (meta de UX a validar com usuários).

## RNF9. Custos

- Custo por organização **previsível e mensurável** (métricas de uso por organização), para precificar planos.
- Canais pagos (WhatsApp/SMS), armazenamento de anexos e relatórios pesados têm **limites por plano**.
- Escolhas de infraestrutura priorizam serviços gerenciados e custo proporcional ao uso na fase inicial (1 cliente), com caminho claro de crescimento.

## Pontos a confirmar
1. As metas de escala (RNF1) estão adequadas, ou você pensa em outro horizonte?
2. Disponibilidade de 99,9% e RPO de 5 min são aceitáveis, considerando o custo de infraestrutura que isso exige?
3. Dados hospedados no Brasil: exigência dos clientes, ou preferência?
4. Há restrição de orçamento de infraestrutura para a fase inicial?
