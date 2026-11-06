# Fase 2: Salão rápido (itens 2.1 a 2.12 do roadmap, depois da fatia vertical)

Status: rascunho 1 (2026-11-06), escrito pelo assistente a partir de `06-roadmap.md` (R5), `02-features.md` (F1, F2, F4, F5, F16), `02d-catalogo-permissoes.md` e do que a fatia vertical entregou (`.scratch/dominio-salao/`). **Não aprovado.** Itens marcados *(proposta)* são decisões minhas que o dono deve confirmar; a seção "Perguntas abertas" lista o que bloqueia.

## Problema
A fatia vertical provou o caminho: entrar, abrir caixa, abrir comanda, lançar rodada, receber **em dinheiro** e fechar, no celular, com fila offline, em duas organizações. Isso ainda não substitui nada do que o restaurante faz numa noite real: só aceita dinheiro, não divide conta, não dá desconto nem cortesia, não troca de mesa, não tem adicionais nem preço promocional, o cardápio é cadastrado produto a produto, só o dono tem permissões úteis (não há rotas de perfis) e o caixa não tem sangria nem suprimento. Sem isso o garçom real não consegue nem o piloto em modo sombra (Marco 2A do roadmap).

## Solução
Completar o salão até o **piloto em modo sombra** e, na sequência, até a Fase 2 inteira, em dois marcos, sem big bang e sempre utilizável:

- **Marco 2A, piloto em modo sombra** (garçons reais, dados não oficiais): tudo o que uma noite de operação exige do garçom e do caixa.
- **Marco 2B, fechamento da Fase 2**: gestão na web (central de comandas, promoções, clientes e fiado, estorno, agenda de funcionamento, reservas).

Regra de ordem herdada do roadmap: lançamento rápido primeiro, o resto depois. Cada ticket termina em estado utilizável e leva seus testes (domínio, banco real, contrato, isolamento entre organizações, matriz de permissões).

## Pré-requisitos (dívidas da Fase 1 que esta fase pisa)
Não são tickets de produto, mas bloqueiam confiança no que vem depois. Ordem sugerida: P1 e P2 antes do primeiro ticket de produto.
- **P1.** PR de legado para `dev-v2` listando F4-0001 a F4-0038 e **rodar o CI do GitHub pela primeira vez** (hoje nenhum gate foi provado fora do computador). Depende de configuração do dono no GitHub.
- **P2.** Revisões independentes dos tickets 06 a 14 do salão **feitas em 2026-11-06, nenhuma BLOCK** (relatórios em `.scratch/dominio-salao/reviews/`). Riscos que viram um ticket de correção `fix` antes do A1: (a) pagamento duplicado quando a conta é reaberta com pagamento na fila (`FecharConta.tsx:73`); (b) rodada online ultrapassa ação enfileirada da mesma comanda (`ComandaPage.tsx:88-110`); (c) `queryClient.clear()` e `limparFila` em todo caminho de saída (cache de permissões e fila vazam entre contas no mesmo celular); (d) `vercel.json` sem rewrite da API em produção; (e) quem recebe/fecha comanda sem `comanda:ver-comandas-de-outros` consegue pagar comanda que não lê (`GestaoDoRecebimento.cs:119,243`); (f) desfazer rodada exige `versao` exata e falha com 409 se outro aparelho lançou na janela de 8 s; (g) duas abas sobrescrevem a fila.
- **P3.** Ligar o despachante do outbox e o agendamento (Quartz): sem isso os eventos `comanda.*`/`pagamento.*` nunca têm consumidor, e a Central em tempo real (2B) e os alertas dependem disso (também é o item 1.9 da Fase 1B).
- **P4.** Perfis de fábrica ou rotas de perfis: decisão registrada como pendência na spec do salão (ver pergunta Q2).

## Fatiamento e ordem dos tickets *(proposta)*
Cada linha vira um ticket `F4-NNNN` (próximo livre: F4-0043 em diante), com a sua branch `tipo/F4-NNNN-resumo` e PR para `dev-v2` (ADR-016). Tamanho: P/M/G como no roadmap.

**Marco 2A (piloto)**
| # | Ticket | Roadmap | Tam. |
|---|---|---|---|
| A1 | Perfis na prática: rotas de perfis e atribuição (`perfil:gerir`, `perfil:ver`, `permissao:atribuir`), perfis modelo "garçom", "caixa", "gerente" criáveis pelo dono em um toque, tela mínima de usuários e perfis | 4.6 parcial, pendência 1A | M |
| A2 | Cardápio completo: categorias em 2 níveis, preço promocional simples (valor e vigência), ativo/inativo, "acabou" manual, preço por unidade (sobrescreve a base) | 2.2 | M |
| A3 | Opções e adicionais (grupos, mín/máx, obrigatório, preço), "mais usados em um toque" no lançamento, observação por item na UI | 2.3, 2.1 | M |
| A4 | Lançamento rápido completo: favoritos do garçom, "repetir última rodada", busca por nome e código, cartão "última vez nesta mesa" (que a fatia deixou de fora), teclado numérico em todo lugar | 2.1 | M |
| A5 | Mesas e áreas: cadastro de áreas, mapa derivado das comandas (status calculado: livre, ocupada, aguardando conta), trocar mesa | 2.6 | M |
| A6 | Comandas completas I: alterar item (quantidade, observação), observação geral, renomear, trocar garçom, nº de pessoas, histórico (linha do tempo), cancelar comanda (sem pagamentos), bloqueio durante fechamento | 2.7 | M |
| A7 | Comandas completas II: desconto (percentual ou valor, item ou comanda), cortesia e brinde (consomem estoque quando existir, distintos em relatório), com **limites por perfil via motor** (`DecidirAsync` com `Value`/`Percent`, limite diário acumulado já existe) | 2.7 | G |
| A8 | Transferir item entre comandas, juntar e separar comandas, dividir conta (por pessoa/assento, por item, partes iguais, valores livres, percentual) com prévia | 2.7, F4.3 | G |
| A9 | Pagamentos: métodos cadastráveis com padrões de fábrica (dinheiro, crédito, débito, Pix, vale, voucher/fiado desativados até 2B), registro **manual** pelo provedor `manual`, pagamento parcial, dividido, misto, gorjeta, troco só em dinheiro; imutável, estorno só em 2B | 2.8 | G |
| A10 | Caixa completo: suprimento, sangria (motivo/destino), conferência por método, tolerância de diferença e **aprovação** com limite, conferência cega, separação de funções com autoaprovação configurável, histórico por operador | 2.10 | M |
| A11 | Comprovantes e pré-conta: visualização/compartilhamento (PDF ou imagem), 2ª via, "conta pedida" que põe a mesa em "aguardando conta"; **sem impressão Bluetooth e sem fiscal** (fica fora de escopo, decisão D7/F7) | 2.11 | M |
| A12 | Importação do cardápio por planilha (modelo, validação linha a linha, prévia, aplicação atômica e idempotente, relatório de erros) | 2.12 | M |
| A13 | Fechamento do 2A: dois locatários de ponta a ponta com todos os fluxos novos, metas medidas, roteiro e ensaio do **piloto em modo sombra** (roteiro de noite, planilha de atritos, como reverter) | marco 2A | M |

**Marco 2B**
| # | Ticket | Roadmap | Tam. |
|---|---|---|---|
| B1 | Notificações em tempo real (SSE) sobre os eventos do outbox, versão da comanda como fonte de ordem | 1.9 (Fase 1B) | G |
| B2 | Central de comandas na web: lista e mapa ao vivo, filtros (garçom, mesa, tempo aberto, número), edição com permissão, fechamento forçado | 2.5 | M |
| B3 | Estorno de pagamento (total e parcial, motivo, volta ao turno certo, limite [L]) e cancelamento de comanda com pagamentos | F4.5, F1.5 | M |
| B4 | Clientes mínimo e consentimento (LGPD): cadastro, busca, duplicidade, vínculo opcional à comanda, mascaramento sem `cliente:ver-dados-pessoais` | 2.9 | M |
| B5 | Fiado e vouchers (depende de B4) | F4.6, F4.7 | M |
| B6 | Promoções e combos: tela própria, pontual/recorrente, horário, unidades, aplicação automática e registro da promoção no item | 2.4 | G |
| B7 | Agenda de funcionamento e regras de horário (semana padrão, exceções, "estender hoje", bloqueio fora do horário), e o resolvedor do dia operacional passando a ler `dia-operacional.*` | 2.10b | M |
| B8 | Reservas pelo caminho da comanda (estado Reservada, "Chegou") | F1 | M |
| B9 | Fechamento da Fase 2: medições com carga simulada (Fase 2 de `07` T7), critério de saída, aprovação do cliente | saída da Fase 2 | M |

A ordem A1 → A13 é a sugerida; A2/A3, A5 e A10 não dependem uns dos outros e podem andar em paralelo se houver mais de um executor (hoje o projeto roda um ticket por vez). B1 pode subir para antes do 2A se o dono quiser ver o celular de um garçom refletindo no outro sem recarregar.

## Decisões de implementação *(propostas, em ordem de impacto)*

### Módulos e fronteiras
- Mantêm-se os quatro módulos do salão (`Cardapio`, `Comandas`, `Pagamentos`, `Caixa`) e as arestas do ADR-014. Novos módulos só quando a fronteira for real: `Clientes` (B4) e `Promocoes` (B6), este dependendo de `Cardapio`. Cada aresta nova entra em `backend/modules.dependencies.json` no mesmo ticket e ganha ADR se mudar o grafo (ADR-017 para clientes e promoções).
- **Orquestração permanece em `Comandas.Application`** (um `IUnitOfWork.ExecuteAsync` por operação): dividir, juntar, transferir e desconto são casos de uso de `Comandas`; `Pagamentos` só registra dinheiro/método; `Caixa` só movimento de turno.
- Estoque (Fase 3) ainda não existe: cortesia e brinde gravam o tipo e o motivo agora e os eventos `comanda.item-cortesia`/`comanda.item-brinde` carregam o necessário para a baixa futura; nada de estoque é inventado aqui.

### Dinheiro e rateio
- Tudo em centavos inteiros, moeda BRL; **divisão em partes iguais com resto determinístico** (o resto vai para as primeiras partes, ordem estável), em **uma função pura** coberta por testes de propriedade (soma das partes = total, nenhuma parte negativa, determinismo). O mesmo vale para desconto proporcional, taxa de serviço sobre desconto e gorjeta.
- Ordem de cálculo fixada e documentada (ADR-018): consumo → desconto de item → desconto de comanda → taxa de serviço → (couvert, se a organização ligar) → gorjeta fora do saldo. Taxa de serviço calcula sobre o consumo **já descontado** *(proposta; confirmar, pergunta Q4)*.
- Pagamento continua imutável (SELECT e INSERT apenas). Troco só existe para dinheiro. Método assíncrono (Pix/TEF) fica **como interface**: o provedor `manual` marca confirmado na hora; o estado `pendente` existe no modelo para não refazer a tabela, sem provedor real.

### Modelo de dados (migrações SQL a partir da próxima numeração livre)
- Mesmo padrão de tenant de sempre (RLS exata, FKs compostas, ids v7, UTC). Tabelas novas previstas: `areas`, `grupos_opcao`, `opcoes`, `produto_grupos_opcao`, `item_opcoes` (opção escolhida, nome e preço congelados), `descontos_comanda`, `metodos_pagamento`, `divisoes`, `movimentos_caixa` (suprimento/sangria), `aprovacoes_diferenca`, `importacoes_cardapio`. Itens ganham `tipo` (`normal`, `cortesia`, `brinde`) e `assento_id` opcional. Detalhe de colunas fica no ticket de cada uma.
- Preço por unidade: tabela de sobrescrita `(organizacao, unidade, produto) → preço`; o preço congelado no item continua sendo a fonte do que foi cobrado.
- `SchemaDrift` por contexto novo; `RlsArchitectureIntegrationTests` cobre as tabelas sem edição.

### Autorização, auditoria, eventos
- Permissões novas seguem o catálogo `02d` com a convenção de hífen: `comanda:alterar-item`, `comanda:cancelar` [A], `comanda:cortesia` [A][L], `comanda:brinde` [A][L], `comanda:desconto` [A][L], `comanda:transferir-item`, `comanda:transferir-mesa`, `comanda:juntar`, `comanda:dividir`, `comanda:renomear`, `comanda:trocar-garcom`, `pagamento:dividir`, `pagamento:registrar-gorjeta`, `metodo-pagamento:gerenciar` [A], `caixa:suprimento` [A][L], `caixa:sangria` [A][L], `caixa:fechar-cego`, `caixa:aprovar-diferenca` [A][L], `cardapio:preco-alterar` [A][L], `cardapio:importar` [A]. Todas passam pelo catálogo exportado e pelo teste da matriz perfil × permissão.
- **Limites [L] pelo motor, nunca pela UI:** valores e percentuais saem da comanda carregada no servidor (`DecisionContext.Value/Percent`), a reserva do limite diário acontece na mesma unidade de trabalho; desconto acima do limite devolve 403 sem motivo.
- Ações de dinheiro e as [A] auditam no mesmo unit of work (`IAuditLog.RecordAsync`), nomes novos só acrescentam em `audit.catalog.txt`. Negação auditada apenas onde vale a trava da cadeia (`sensitive: true`): cortesia, brinde, desconto acima do limite, sangria, aprovação.
- Eventos novos no outbox (nomes estáveis): `comanda.item-alterado`, `comanda.item-cortesia`, `comanda.item-brinde`, `comanda.desconto-aplicado`, `comanda.transferida`, `comanda.juntada`, `comanda.dividida`, `comanda.conta-pedida`, `comanda.cancelada`, `caixa.suprimento`, `caixa.sangria`, `caixa.diferenca-aprovada`, `cardapio.importado`.
- Opções em camadas novas *(proposta)*: `venda.desconto-max-percentual` (padrão por perfil via limite, não aqui), `caixa.tolerancia-diferenca-centavos` (padrão 500), `caixa.conferencia-cega` (booleano), `caixa.permitir-autoaprovacao` (booleano), `comanda.numeracao` (`reinicia-por-dia`|`continua`, que a F1 já descreve), `venda.couvert-centavos` (0 = desligado).

### API, contrato e cliente
- Todas as rotas com `.WithName`, respostas tipadas, `.WithBodyLimit`, `.RequireSession()` e `.RequirePermission(...)`, nenhuma com organização/unidade do cliente. Regenerar `openapi.json` e o cliente a cada ticket que muda rota (gate `openapi-contract`, com `oasdiff` bloqueando quebra).
- Concorrência otimista (`versao`) em toda escrita na comanda, inclusive dividir/juntar/transferir (que tocam **duas** comandas: travar em ordem de id para não dar deadlock, ambas sobem de versão). Operações idempotentes por id do cliente: rodada, pagamento, divisão, importação.

### Frontend (`frontend/app`, rota `/salao`; gestão na web chega só em 2B)
- Mesma arquitetura (React/Vite, TanStack Query/Router, design system do `DESIGN.md` aprovado em F4-0042). Novas telas do 2A: mapa de mesas, opções no lançamento, favoritos/repetir rodada, desconto/cortesia, dividir conta, receber com métodos, caixa (sangria/suprimento/fechamento cego), pré-conta, perfis/usuários (a gestão de usuários continua sendo tela do dono no celular nesta fase; versão web ampla é 4.6).
- Fila offline: **tudo que o garçom faz sem sinal continua indo para a fila** (lançar, repetir rodada, desfazer); **operações de dinheiro grande e de aprovação (desconto acima de X, cortesia, dividir, estorno, sangria) exigem conexão** *(proposta; evita fila com decisão de limite stale; pergunta Q5)*.
- Meta de toques: lançar item comum em 2 toques; abrir comanda e primeiro item em <10 s (já medido ~1,1 s). Novas metas medidas no A13: dividir em partes iguais em ≤ 4 toques; receber misto (2 métodos) em ≤ 8 toques.
- Design: tokens do protótipo; qualquer tela nova sem referência autoral segue o protótipo `index.html` e passa pelo `design-reviewer` (screenshots 390x844 e 360x740, claro e escuro).

## Seams e testes (RNF7)
- **Domínio puro:** rateio e arredondamento (testes de propriedade), ordem de cálculo, estados da comanda com as novas transições, política de limite, tolerância de diferença.
- **Banco real (Testcontainers):** concorrência de dividir/juntar/transferir (duas comandas, sem deadlock, sem perder item), idempotência por id, reserva de limite diário sob concorrência, RLS das tabelas novas, append-only de `pagamentos`/movimentos.
- **Contrato:** OpenAPI + `x-permission` + `oasdiff`; matriz perfil × permissão para cada permissão nova; `ClientContextInputTests`.
- **Dois locatários por HTTP** (`DoisTenantsEndpointsTests` estendido): cada fluxo novo roda nas duas organizações e a varredura de tabelas continua zerada entre elas.
- **Navegador (Playwright contra a API real em `docker compose`):** fluxos críticos nos dois viewports; o serviço `seed` precisa funcionar no Docker (hoje cai por falta de `libgssapi_krb5` na imagem `aspnet`: corrigir no A13 ou antes, pois o seed rodou só pelo `dotnet` do host).

## Critérios de aceite
**Marco 2A (piloto)**
1. Um garçom, um caixa e um gerente criados por perfil modelo conseguem, **sem o dono**, rodar uma noite simulada completa: abrir turno, abrir comandas com e sem mesa, lançar com adicionais, repetir rodada, dar desconto/cortesia dentro do limite e ser **negado** acima dele, transferir, juntar, dividir (cada uma das formas), receber com mais de um método, fechar, fazer sangria e fechar o caixa com diferença aprovada.
2. Todas as metas de toque e tempo do roadmap atingidas em aparelho real (não só no emulador); divisão validada por testes de propriedade; dois aparelhos na mesma comanda sem perda.
3. Cardápio real do restaurante importado por planilha sem edição manual de banco, com relatório de erros legível.
4. Todo CI verde no GitHub (primeira rodada real), suítes completas verdes, `dotnet format` limpo, revisão independente de cada ticket sem BLOCK.
5. Roteiro do piloto aprovado pelo dono, com plano de volta (os dados do sombra são descartáveis e não alimentam nada oficial).

**Marco 2B (saída da Fase 2, do roadmap)**
Metas de toques e de tempo atingidas em uso real; dois dispositivos na mesma comanda sem perda; divisão de conta por testes de propriedade; metas de desempenho medidas com carga; aprovação do cliente sobre o fluxo.

## Fora de escopo
Estoque, ficha técnica e baixa automática (Fase 3); relatórios nominais, dashboard e contas a pagar (Fase 4); comissões; Control Tower e cobrança (Fase 6); KDS, impressão por praça e Bluetooth (Fase 9); NFC-e e TEF/Pix automático (Fase 10) — fica só a interface do provedor; QR do cliente; ditado de voz; leitura de código de barras *(proposta: mantém fora até haver teste em aparelho real)*; offline completo (D7); app nativo; login com Google e convite pela tela do celular (existem como rotas).

## Perguntas abertas (bloqueiam ou mudam o desenho)
- **Q1. Piloto: quais métodos de pagamento o restaurante usa de fato** (dinheiro, crédito, débito, Pix, vale-refeição)? Define o escopo do A9 e se fiado/voucher mesmo precisam esperar 2B.
- **Q2. Perfis para o piloto:** rotas de perfis (A1 completo) ou só três perfis modelo semeados por organização? *(proposta: modelos + rotas mínimas de atribuição; edição fina de regras fica para 4.6)*.
- **Q3. Central na web (2.5) e SSE (1.9) antes ou depois do piloto?** *(proposta: depois; o piloto roda só no celular)*.
- **Q4. Taxa de serviço sobre consumo bruto ou líquido do desconto? Couvert existe no restaurante?**
- **Q5. Operações grandes (desconto, cortesia, dividir, sangria) só online?** *(proposta: sim)*.
- **Q6. Quem pode aprovar diferença de caixa no restaurante e a autoaprovação do dono está liberada?**
- **Q7. Planilha do cardápio:** o restaurante já tem uma? Qual formato (CSV ou XLSX)? Precisamos de uma amostra real para o A12.
- **Q8. Identidade visual v6 ou v7** (herdada da Fase 1B) afeta tokens finais; os valores do protótipo continuam provisórios até lá.
- **Q9. Hospedagem do piloto:** o piloto exige `staging` no ar (Railway/Vercel configurados pelo dono, P1 a P13 do ticket 11 da Fase 0). Sem isso o piloto só roda na rede local.
