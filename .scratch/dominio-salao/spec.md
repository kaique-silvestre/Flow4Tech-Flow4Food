# Domínio do salão e fatia vertical `/salao` (Fase 1B, itens 1.12 e pré-requisito do 1.9)

Spec do roadmap 1.12: "na rota `/salao`, entrar, abrir comanda, lançar item, pagar em dinheiro, fechar, abrir/fechar caixa, com permissão, auditoria e eventos". Inclui o modelo de domínio (cardápio mínimo, mesa, comanda, item, pagamento em dinheiro, caixa) e a tela do celular. Origem: `02-features.md` F1, F2 (mínimo), F4, F5; `05-ux-design.md` U1, U2, U4, U7, U18; `06-roadmap.md` R4; contrato visual em `docs/design/DESIGN.md` seção "Salão (mobile slice)". Depende da Fase 1A (sessões, permissões, auditoria, outbox, configurações em camadas).

Decisões do dono já tomadas (2026-11-05): escopo backend + tela; cardápio mínimo agora; pagamento só em dinheiro com troco; seams HTTP + Domínio (e UI, ver Seams).

## Problema

O restaurante lança pedidos no pico, de pé, com uma mão: "marcar na comanda" não cabe no tempo que existe. A plataforma base (organização, identidade, permissões, auditoria, eventos) está pronta, mas não há nada que um garçom possa usar: nenhuma comanda, nenhum produto, nenhum caixa e nenhuma tela. Sem uma fatia ponta a ponta no celular, a arquitetura não está provada onde importa (permissão, auditoria e isolamento de um fluxo real) e o cliente não tem o que testar.

## Solução

Um garçom entra no celular, o dono ou gerente abre o caixa, o garçom abre uma comanda por um teclado numérico, lança itens do cardápio em poucos toques, envia a rodada (com desfazer por 8 segundos), e na hora de pagar fecha a conta em dinheiro, com troco, até o saldo zerar. Toda ação respeita as permissões, é auditada na cadeia por hash, publica eventos no outbox e nunca vaza dados entre organizações. A tela funciona em queda curta de sinal: ações ficam numa fila visível e reenviam sem duplicar. Tudo isso em módulos novos (`Cardapio`, `Comandas`, `Pagamentos`, `Caixa`) que seguem as regras de módulo, de RLS e de rota da plataforma.

## User stories

Entrada e contexto
1. Como garçom, quero entrar com meu usuário operacional e senha no celular, para começar o turno sem depender de e-mail.
2. Como garçom com senha temporária, quero ser levado direto à troca de senha, para só então ver as comandas.
3. Como garçom ligado a mais de uma unidade, quero escolher a unidade ativa, para lançar na casa certa.
4. Como dono, quero que o garçom nunca veja nem lance em comandas de outra organização ou de unidade a que não está ligado, para que os dados de cada casa fiquem separados.

Caixa
5. Como operador de caixa, quero abrir o caixa informando o fundo de troco, para começar a receber.
6. Como operador de caixa, quero que o fundo de troco não conte como venda, para que o faturamento do turno seja verdadeiro.
7. Como operador de caixa, quero fechar o caixa informando o dinheiro contado, para ver o esperado, a diferença (falta ou sobra, em palavras) e encerrar o turno.
8. Como dono, quero que um operador não consiga abrir dois turnos ao mesmo tempo na mesma unidade, para não haver recebimento sem dono.
9. Como dono, quero que a unidade possa exigir caixa aberto para receber pagamento, para que nenhum dinheiro entre sem turno.
10. Como dono, quero que a abertura e o fechamento de caixa fiquem na auditoria com valores, para conferir depois.

Cardápio mínimo
11. Como gerente, quero cadastrar produto com nome, código curto, preço em centavos e categoria, e ativar ou desativar, para que o garçom lance só o que existe.
12. Como garçom, quero buscar produto por nome ou código, para lançar rápido.
13. Como garçom, quero ver os frequentes no topo (mais vendidos da unidade), para lançar em dois toques.
14. Como dono, quero que o preço mude sem afetar comandas já lançadas, para que o histórico não reescreva a conta.

Mesas
15. Como gerente, quero cadastrar mesas da unidade (número ou nome, capacidade opcional), para o garçom escolher.
16. Como dono, quero ligar ou desligar o uso de mesas por unidade, para que um bar de cartão de comanda não precise de mesa.
17. Como garçom, quero ver quais mesas estão livres e ocupadas, calculado das comandas abertas, para não abrir duas comandas sem querer.
18. Como gerente, quero que uma mesa possa ter várias comandas, para cada cliente ter a sua.

Comanda
19. Como garçom, quero abrir comanda digitando só o número da mesa (ou "dar um nome"), para lançar o primeiro item em menos de 10 segundos.
20. Como garçom, quero que a comanda receba um número de exibição (#12) sequencial por unidade, que reinicia a cada dia operacional, para dizer a ordem de abertura sem confusão.
21. Como garçom, quero lançar item com quantidade e observação, para registrar "sem gelo".
22. Como garçom, quero montar uma rodada com vários itens e enviar de uma vez, para o pedido sair junto.
23. Como garçom, quero desfazer a rodada por 8 segundos após enviar, para corrigir um erro de dedo.
24. Como garçom, quero repetir a última rodada, para pedir "mais uma" em um toque.
25. Como garçom, quero cancelar um item informando o motivo, para a conta refletir a realidade e o motivo ficar registrado.
26. Como garçom, quero que dois aparelhos lançando na mesma comanda não percam itens, para dois garçons atenderem a mesma mesa.
27. Como garçom, quero ver "Esta comanda foi alterada" sem perder o que montei, para continuar depois do aviso.
28. Como garçom, quero ver só as minhas comandas, a não ser que eu tenha permissão de ver as dos outros, para não mexer na mesa do colega.
29. Como gerente, quero ver as comandas de todos os garçons da unidade, para acompanhar o salão.
30. Como dono, quero que preço e nome do produto fiquem congelados em cada item, para o relatório de ontem não mudar com o cardápio de hoje.

Conta e pagamento em dinheiro
31. Como garçom, quero fechar a conta vendo consumo, taxa de serviço e total, para dizer o valor ao cliente.
32. Como gerente, quero que a taxa de serviço (percentual da unidade) venha incluída e que removê-la exija permissão e fique auditada, para controlar abuso.
33. Como operador de caixa, quero registrar o dinheiro recebido e ver o troco em destaque, para devolver certo.
34. Como operador de caixa, quero receber em dinheiro de forma parcial (parte agora, resto depois), para o cliente que paga em duas vezes.
35. Como dono, quero que a comanda só feche com saldo zero e que o pagamento seja imutável, para o caixa bater.
36. Como dono, quero que a comanda fechada não aceite mais item nem pagamento, para o fechamento ser definitivo.
37. Como dono, quero que cada pagamento em dinheiro entre no esperado do turno do caixa, para a conferência fechar.

Falhas de rede
38. Como garçom, quero continuar lançando durante uma queda curta de sinal e ver a fila de ações pendentes, para não perder pedido.
39. Como garçom, quero que reenviar a mesma rodada ou o mesmo pagamento não duplique, para o reenvio automático ser seguro.

Segurança e auditoria
40. Como dono, quero que nenhuma rota aceite organização ou unidade vindas do cliente, para ninguém lançar em contexto alheio.
41. Como dono, quero que toda ação que muda estado (abrir/fechar caixa, abrir comanda, lançar, cancelar item, desfazer rodada, remover taxa, pagar, fechar) gere um registro de auditoria na mesma transação, para a cadeia contar a verdade.
42. Como dono, quero que nenhum registro de auditoria ou log carregue segredo, para a regra de dados sensíveis seguir valendo.
43. Como dono, quero que negações de permissão em ações sensíveis sejam auditadas, para ver tentativas.
44. Como integrador futuro, quero eventos `comanda.*`, `pagamento.*` e `caixa.*` no outbox, para cozinha, estoque e notificações consumirem depois sem mexer aqui.

## Decisões de implementação

### Módulos e fronteiras
- **Quatro módulos novos**, cada um com Domain, Application, Infrastructure e Interface (padrão `Flow4Tech.<Módulo>.<Camada>`): `Cardapio`, `Comandas` (inclui mesa), `Pagamentos`, `Caixa`. Todos entram em `modules.dependencies.json`.
- **Arestas permitidas:** `Comandas → Cardapio`, `Comandas → Pagamentos`, `Pagamentos → Caixa`. `Cardapio` e `Caixa` não dependem de nenhum módulo de negócio. Ninguém depende de `Comandas` (só por eventos). Sem ciclos. Cada módulo só usa a API pública (contratos de Application) do outro.
- Orquestração do recebimento: o caso de uso "receber pagamento da comanda" vive em `Comandas.Application`: dentro de **um** `IUnitOfWork.ExecuteAsync` carrega a comanda, pergunta `DecidirAsync`, chama o contrato público de `Pagamentos` (que por sua vez registra o movimento no turno via contrato público de `Caixa`), audita e grava o evento. Fechar comanda confere saldo zero no mesmo unit of work.
- Escrever **ADR-014** (módulos do salão e suas dependências, incluindo por que mesa fica em `Comandas` e por que `Pagamentos → Caixa`) e **ADR-015** (PWA do salão: fila de ações, idempotência por id gerado no cliente, sem offline completo, ligado a D7/A11). Registrar a ordem de decisão em `docs/adr/`.
- Infra compartilhada reutilizada sem mudança: `TenantDatabase`/`IUnitOfWork`, `IAuditLog`, `OutboxWriter`, `ISettings`, `IOperatingDayResolver`, `IAuthorizationEngine`. Nenhum módulo novo referencia `Npgsql` nem cria conexão fora do `TenantDatabase`.

### Modelo de dados (migrações SQL numeradas a partir de `0015_`)
Todas as tabelas são de tenant: `organizacao_id uuid NOT NULL`, `ENABLE` e `FORCE ROW LEVEL SECURITY`, política exata de `app_organizacao_id()`, referências filhas por chave composta `(organizacao_id, pai_id)`. Dinheiro sempre `bigint` em centavos mais moeda `BRL`; ids UUID v7; tempos UTC.
- **`categorias`** e **`produtos`** (Cardapio): nome, código curto único por organização, preço em centavos, moeda, ativo, categoria, `versao`. Sem preço promocional, sem opções, sem ficha técnica (Fase 2.2/2.3).
- **`mesas`** (Comandas): unidade, número ou nome único por unidade, capacidade opcional, ativa. Status **não é coluna**: é calculado das comandas abertas.
- **`comandas`**: unidade, número de exibição (único por unidade e dia operacional; reinicia no dia operacional, padrão), identificação (número da mesa ou nome livre), `mesa_id` opcional, canal (valor padrão `salao`, reservado), garçom responsável, número de pessoas opcional, estado (`aberta`, `fechada`, `cancelada`), `aberta_em`, `fechada_em`, `taxa_servico_percentual` congelado ao abrir, `taxa_servico_removida` (boolean), `versao` (concorrência otimista). Reaberta e reserva ficam fora desta fatia.
- **`rodadas`**: id gerado no cliente (idempotência), comanda, enviada_em, desfeita_em. **`itens_comanda`**: comanda, rodada, produto, **nome e preço congelados**, quantidade, observação, estado (`lancado`, `cancelado`), motivo do cancelamento, quem cancelou.
- **`turnos_caixa`**: unidade, operador, `aberto_em`, `fundo_troco_centavos`, `fechado_em`, `dinheiro_contado_centavos`, `esperado_centavos`, `diferenca_centavos`. Restrição de unicidade parcial: no máximo um turno aberto por operador e unidade. **Não há cadastro de caixas físicos nesta fatia**: o turno é do operador (opção "um caixa por operador", F5); o cadastro de caixas e a conferência cega entram na Fase 2.10.
- **`pagamentos`** (Pagamentos): comanda, turno, método (`dinheiro` apenas), `valor_recebido`, `valor_aplicado`, `troco` (recebido menos aplicado), operador, id gerado no cliente (idempotência), `criado_em`. **Imutável**: o app tem só SELECT e INSERT (como `auditoria_registros`); correção é estorno, que fica fora desta fatia.
- Tabelas de apoio: `comanda_numeracao` (contador por unidade e dia operacional, linha travada na transação) e linhas de idempotência por id de cliente nas rodadas e nos pagamentos.
- `RlsArchitectureIntegrationTests` e `SchemaDrift` cobrem as tabelas sem edição; cada contexto EF novo ganha seu teste de `SchemaDrift`.

### Regras de domínio (módulo `Comandas`, puro, sem I/O)
- Estados da comanda: `aberta → fechada | cancelada`. Lançar item e receber pagamento só em `aberta`. Cancelar comanda só sem pagamentos (fora desta fatia: ver Fora de escopo).
- Item: preço e nome copiados do produto no lançamento; quantidade inteira maior que zero; produto inativo não lança; cancelar exige motivo da lista padronizada (cliente desistiu, erro de lançamento, item indisponível, outro) e, em "outro", texto.
- Rodada: conjunto de itens enviado junto, com id do cliente; **desfazer** cancela a rodada inteira, só dentro da janela (padrão 8 s, medida no servidor com `IClock`), só pelo mesmo usuário e só se nenhum item da rodada já foi cancelado por outro caminho. O motivo do cancelamento é fixo ("desfeito pelo garçom").
- Saldo = consumo (itens `lancado`) + taxa de serviço − aplicado em pagamentos. A comanda só fecha com saldo zero.
- Taxa de serviço: percentual por unidade (opção em camadas, padrão 10, de 0 a 20), congelado na comanda ao abrir; calculada sobre o consumo em centavos com arredondamento "meio para cima" numa única função pura; remover a taxa exige `comanda:remover-taxa-servico` e é auditado.
- Troco: `troco = valor_recebido − valor_aplicado`, nunca negativo; `valor_aplicado` nunca passa do saldo; recebimento parcial é permitido.
- Número de exibição: próximo número da unidade no dia operacional; atribuído na mesma transação da abertura, sob trava, sem buracos nem repetição sob concorrência.
- **Dia operacional** vem de `IOperatingDayResolver` (padrão: fuso da unidade, virada às 05:00); a pendência de ele ler as opções `dia-operacional.*` (roadmap 2.10b) continua fora desta fatia.

### Caixa e Pagamentos
- Abrir turno: operador, fundo de troco (centavos, zero permitido), um aberto por operador e unidade. Fechar turno: contagem do dinheiro obrigatória (pode ser zero); `esperado = fundo + recebimentos aplicados em dinheiro` (suprimento, sangria e estorno não existem nesta fatia); diferença exibida em palavras ("Confere", "Falta R$ x", "Sobra R$ x"); **sem aprovação de diferença nesta fatia**: o fechamento sempre conclui e registra a diferença (a aprovação por tolerância e limite entra na 2.10).
- Receber pagamento em dinheiro exige turno aberto do próprio operador na unidade ativa (opção da unidade `caixa.exigir-caixa-aberto`, padrão ligado; desligada, o pagamento fica sem turno e fora do esperado de qualquer caixa).
- `Pagamentos` expõe um contrato de **provedor** (`iniciar`, `confirmar`) com um único provedor `manual` síncrono, mantendo o encaixe de TEF/Pix da F4 sem implementar nenhum.

### Autorização, auditoria, eventos
- **Permissões novas** (constantes `resource:action` em classes `[PermissionCatalog]` de cada módulo; o catálogo exportado `backend/permissions.catalog.txt` é regenerado): produto e categoria (`produto:ver`, `produto:gerenciar`, nomes finais conforme `02d`), `mesa:ver`, `mesa:gerenciar`, `comanda:ver`, `comanda:criar`, `comanda:lancar-item`, `comanda:cancelar-item` [A], `comanda:desfazer-rodada`, `comanda:remover-taxa-servico` [A], `comanda:ver-comandas-de-outros`, `comanda:receber-pagamento`, `comanda:fechar`, `pagamento:registrar`, `pagamento:ver`, `caixa:ver`, `caixa:abrir`, `caixa:fechar`, `caixa:ver-todos`. A convenção do código usa hífen, não o sublinhado do `02`.
- Cada rota declara `.RequirePermission(...)` e `.RequireSession()`; nenhuma rota recebe organização ou unidade do cliente (`ClientContextInputTests`). Ações [A] e as de dinheiro usam `sensitive: true` apenas onde auditar a negação vale a trava da cadeia; cancelar item, remover taxa e abrir/fechar caixa são sensíveis.
- Nenhuma permissão desta fatia tem limite acumulado; o motor continua sendo a única porta (`DecidirAsync`), sem ramificar por nome de perfil. A visibilidade "só as minhas comandas" é decidida pela permissão `comanda:ver-comandas-de-outros`, não por consulta a perfil.
- **Auditoria** (`IAuditLog.RecordAsync` dentro do unit of work, constantes `recurso:acao` em classes `[AuditCatalog]`, `backend/audit.catalog.txt` só cresce): `caixa:abrir`, `caixa:fechar`, `comanda:abrir`, `comanda:lancar-rodada`, `comanda:desfazer-rodada`, `comanda:cancelar-item`, `comanda:remover-taxa-servico`, `comanda:fechar`, `pagamento:registrar`, `produto:criar`, `produto:alterar`, `mesa:criar`. Valores em `Before`/`After` seguem a regra `SensitiveData`.
- **Eventos** no outbox, na mesma transação (nomes estáveis, nunca renomeados): `comanda.aberta`, `comanda.rodada-enviada`, `comanda.rodada-desfeita`, `comanda.item-cancelado`, `comanda.fechada`, `pagamento.registrado`, `caixa.aberto`, `caixa.fechado`. Sem consumidor nesta fatia; os eventos carregam organização, unidade, ids e valores em centavos.
- **Opções em camadas novas** (`[SettingsCatalog]`, `backend/settings.catalog.txt`): `operacao.usar-mesas` (booleano, organização e unidade, padrão ligado), `venda.taxa-servico-percentual` (número 0 a 20, organização e unidade, padrão 10), `comanda.janela-desfazer-segundos` (inteiro, somente sistema, padrão 8), `caixa.exigir-caixa-aberto` (booleano, organização e unidade, padrão ligado).

### API e contrato
- Rotas (todas com `.WithName`, respostas tipadas, `.WithBodyLimit`, sem corpo com contexto): cardápio (listar, buscar, frequentes, criar e alterar produto e categoria), mesas (listar com status calculado, criar), comandas (abrir, listar abertas, detalhe, lançar rodada, desfazer rodada, cancelar item, remover taxa, fechar), pagamentos (registrar em dinheiro), caixa (turno atual, abrir, fechar). Rotas mutáveis aceitam o id gerado no cliente (rodada, pagamento) e respondem de forma idempotente a repetições.
- **Concorrência otimista:** toda escrita na comanda carrega a `versao` que o cliente viu; versão velha devolve 409 com a versão atual, e o app mostra o aviso sem perder a rodada montada.
- Depois de mudar rotas: regenerar `openapi.json` e o cliente TypeScript e commitar os dois (gate `openapi-contract`); `x-permission` em cada operação.
- Os endpoints de SSE e a central web ficam de fora (1.9, 2.5).

### Frontend (`frontend/app`, rota `/salao`)
- App React/Vite na rota `/salao`, instalável (PWA), mobile-first, usando o cliente gerado e o pacote `design-system` (tokens e componentes do `DESIGN.md`). Estado de servidor com TanStack Query; formulários com React Hook Form + Zod.
- **Fila de ações pendentes**: toda escrita é enfileirada no cliente com o id gerado (UUID v7), enviada em ordem, reenviada automaticamente com recuo, visível ao usuário; reconexão não duplica porque o servidor é idempotente. Offline completo (abrir comanda sem rede e sincronizar horas depois) fica fora (D7).
- Tokens de acesso curtos e refresh por cookie `__Host-` já existem; o app renova a sessão a partir de um único lugar (regra do refresh rotativo) e trata 401 como "entrar de novo", 403 como "sem permissão" sem mostrar motivo.
- `frontend/design-system` ganha só o que a fatia usa (botão, chip, abas, folha, teclado, linha de item, barra de rodada, aviso de offline), a partir dos tokens do `DESIGN.md`; o catálogo completo de componentes fica para depois.
- Sem rota `control`; sem `/salao` para quem não tem `comanda:ver`.

## UI Contract
- registro: product (direção Minimalist com toques Technical, intensidade internal), conforme `docs/design/DESIGN.md`
- telas:
  - **Entrar** → `/salao/entrar` → DESIGN.md "Salão (mobile slice)" › flow 1 → refs: nenhuma (autoral) → estados: carregando (botão), erro genérico único ("Usuário ou senha inválidos", sem diferenciar), bloqueado (429 com tempo de espera), captcha exigido (428) → fluxo crítico: entrar com senha temporária cai na troca de senha.
  - **Trocar senha / Escolher unidade** → `/salao/conta` → mesma seção → refs: nenhuma (autoral) → estados: erro de política de senha, lista de unidades vazia (conta sem unidade: mensagem e sair).
  - **Comandas** (lista) → `/salao` → seção "Salão" › components e states → refs: nenhuma capturada da lista (o protótipo tem a aba, a captura falhou; ver Notas) → estados: carregando (esqueleto), vazio ("Nenhuma comanda aberta. Toque em Nova comanda."), erro (banner com "Tentar novamente"), offline (aviso + fila) → fluxo crítico: nova comanda.
  - **Nova comanda** (folha com teclado) → `/salao` (folha) → refs: `docs/design/refs/salao/04.png` → estados: botão OK desabilitado sem valor, mesa ocupada (mostra a comanda existente e oferece abrir outra), `Usar mesas` desligado (campo "Comanda 7", não "Mesa 7").
  - **Comanda** → `/salao/comandas/:id` → seção "Salão" › structure → refs: `02.png`, `07.png` (tema escuro com rodada montada), `12-offline.png`, `13-fila.png` → estados: carregando, comanda alterada em outro aparelho, produto inativo, sem permissão (ação ausente), rodada vazia (Enviar ausente) → fluxos críticos: lançar em dois toques, repetir rodada, enviar, desfazer em 8 s, cancelar item com motivo.
  - **Enviar rodada** (folha) → `/salao/comandas/:id` (folha) → refs: `03.png` → estados: enviando (inline), falha (a rodada continua montada).
  - **Fechar conta** (folha) → `/salao/comandas/:id` (folha) → refs: `05.png` (somente a estrutura; só o botão Dinheiro e a linha de troco) → estados: valor menor que o saldo (pagamento parcial, "restará R$ x"), troco em destaque, caixa fechado ("Abra o caixa para receber") → fluxo crítico: dinheiro recebido, troco, confirmar, comanda Fechada.
  - **Caixa** (abrir e fechar turno) → `/salao/caixa` → seção "Salão" › conflicts (sem referência) → refs: nenhuma (autoral, regras de U18) → estados: sem turno (cartão "Abrir caixa" com fundo de troco), com turno (esperado visível porque a fatia não tem conferência cega), fechado com diferença em palavras.
- fora do DESIGN.md: nenhum token ou padrão novo; a barra de rodada e a fila de ações pendentes já estão registradas como assinatura.
- Referências faltando: lista de comandas, entrar, trocar senha e unidade, caixa. O dono aceitou o desenho autoral para essas telas e aprova pelos screenshots do primeiro ticket de UI (o `DESIGN.md` registra `approved: pending`); imagens de referência podem ser acrescentadas em `docs/design/refs/salao/` a qualquer momento.

## Seams
tdd-scope: required (domínio, HTTP, UI) / not-applicable (migrações e configuração, cobertas pelos testes de arquitetura e de esquema existentes)

1. **HTTP com PostgreSQL real** (seam principal, existente): xUnit em `backend/tests/Flow4Tech.Tests`, classes `*Endpoints*` (rodam como integração com Testcontainers `postgres:17-alpine`). Observa: status e corpo de cada rota, linhas gravadas pela credencial `flow4tech_app` sob RLS, registro de auditoria encadeado, eventos no outbox, 401/403/409, idempotência, isolamento entre duas organizações. Prior art: `DoisTenantsEndpointsTests`, `RequestContextEndpointsTests`, `SessoesEndpointsTests`, `PermissoesEndpointsTests` (sessões reais por `Sessoes.IniciarAsync`).
2. **Domínio puro** (seam novo, sem I/O, sem banco): xUnit em testes unitários dos módulos, sem relógio nem aleatoriedade ambientes (`IClock` injetado). Observa: transições da comanda, saldo, taxa de serviço e arredondamento em centavos, troco, janela de desfazer, numeração. Justificativa: essas regras são numéricas e de borda, e testá-las só por HTTP seria lento e indireto. Prior art: `KernelSmokeTests`, `GrantGuardTests` (regras puras sem banco).
3. **UI no navegador** (seam novo, **confirmado pelo dono em 2026-11-05**): vitest com testing-library para lógica de estado, formatação em reais e componentes (fila de ações, barra de rodada, teclado); Playwright em 390x844 e 360x740 com screenshots para os fluxos críticos (entrar, abrir caixa, abrir comanda, lançar e enviar, desfazer, fechar em dinheiro, offline e reconexão) com a API real em `docker compose`. Observa: o que o garçom vê e toca, nunca o interior dos componentes. Prior art: nenhum (o front não tem testes; `pnpm -r --if-present test` passa a existir).
- Os testes de arquitetura existentes continuam como portões: fronteiras de módulo, rota sem permissão declarada, catálogo de permissões e de auditoria append-only, `RlsArchitectureIntegrationTests`, `ClientContextInputTests`, `ProfileBranchingTests`, ban de `Npgsql` e de `DbContext` fora de Data.

## Decisões de teste
- Um bom teste aqui observa comportamento externo: uma requisição HTTP e o que o garçom vê, ou uma regra pura com entrada e saída; nunca chama método interno de handler nem lê tabela fora do que a API expõe, exceto para provar RLS, imutabilidade e auditoria.
- Matriz de permissão: o teste de contrato do motor continua exigindo que todo par perfil × permissão do catálogo esteja decidido; as permissões novas entram nela. Cada rota ganha um teste do caso permitido e do caso negado/cruzando organização, como manda "Writing a route the secure way".
- Concorrência de verdade, não simulada: dois clientes lançando na mesma comanda e duas aberturas simultâneas na mesma unidade (numeração sem buraco e sem repetição), com PostgreSQL real.
- Idempotência: o mesmo id de rodada ou de pagamento repetido N vezes produz um registro e a mesma resposta.
- Tempo e dinheiro: relógio injetado; nenhum `float`/`decimal` para valores armazenados (teste de arquitetura já cobre `float`; acrescentar a verificação nos contratos novos).
- Cada contexto EF novo tem teste de `SchemaDrift` e cada consulta crítica um teste de `ToQueryString()` seguindo `TenantDbContextIntegrationTests`.
- Dois locatários nos testes de HTTP: o teste de ponta a ponta existente é estendido (ou um irmão) com duas organizações usando o fluxo completo, e a varredura de todas as tabelas com `organizacao_id` continua exigindo zero linhas cruzadas e zero sem contexto.
- Teste da tela: sem mock de rede nos fluxos críticos do Playwright; o simulador de queda de sinal usa o próprio navegador (offline do contexto) e confere que nada duplica.
- Sem as suítes de PostgreSQL rodando, nenhum critério de banco conta como verificado (aprendizado da 1A: "done-local" não é "done").

## Critérios de aceite
Cada linha vira um checkbox de ticket.
1. Existem os módulos `Cardapio`, `Comandas`, `Pagamentos` e `Caixa` com as quatro camadas, declarados em `modules.dependencies.json` com as arestas `Comandas → Cardapio`, `Comandas → Pagamentos`, `Pagamentos → Caixa` e nenhuma outra; `backend/tests/Architecture` passa.
2. Toda tabela nova tem `organizacao_id`, RLS forçada e a política exata; `RlsArchitectureIntegrationTests` passa sem edição de exceções; `pagamentos` aceita só SELECT e INSERT para `flow4tech_app` (UPDATE e DELETE falham com 42501).
3. Um operador não abre dois turnos na mesma unidade; abrir com fundo de troco zero é aceito; o fundo não aparece como venda.
4. Receber dinheiro sem turno aberto é recusado quando `caixa.exigir-caixa-aberto` está ligada; recebido menor que o saldo vira pagamento parcial; recebido maior devolve `troco = recebido − aplicado`; `aplicado` nunca passa do saldo.
5. Fechar comanda com saldo diferente de zero é recusado; fechar com saldo zero muda para `fechada`; item e pagamento em comanda fechada são recusados.
6. Fechar turno calcula `esperado = fundo + recebimentos em dinheiro aplicados`, registra a diferença e devolve texto "Confere", "Falta R$ x" ou "Sobra R$ x".
7. Item lançado congela nome e preço; alterar o produto depois não muda a comanda; produto inativo não lança.
8. Cancelar item sem motivo é recusado; com motivo, o item sai do consumo e o motivo fica na auditoria.
9. Desfazer rodada funciona dentro de 8 segundos (relógio injetado), por quem a enviou, e é recusado depois da janela ou por outro usuário.
10. Duas aberturas de comanda simultâneas na mesma unidade e dia operacional recebem números distintos e sem buraco; no dia operacional seguinte o número reinicia.
11. Taxa de serviço: percentual congelado ao abrir; cálculo em centavos com arredondamento "meio para cima"; remover exige `comanda:remover-taxa-servico`, é auditado e 403 sem a permissão.
12. Repetir o mesmo id de rodada ou de pagamento (1 a 5 vezes) produz um único registro e a mesma resposta.
13. Escrita com `versao` desatualizada devolve 409 com a versão atual e não altera nada; nenhum item de nenhum dos dois clientes se perde em lançamento concorrente.
14. Sem `comanda:ver-comandas-de-outros`, a listagem devolve só as comandas do próprio garçom; com a permissão, todas as da unidade ativa; nunca as de outra unidade ou organização (zero linhas cruzadas com duas organizações de teste).
15. Toda rota nova declara `.RequirePermission` e `.RequireSession` (ou está na lista pública conhecida), nenhuma lê `organizacaoId`/`unidadeId` do cliente, e o catálogo exportado de permissões coincide com o do código.
16. Cada ação listada na seção de auditoria gera exatamente um registro na mesma transação (desfazer a transação desfaz o registro), a cadeia verifica como válida, e nenhum campo contém segredo.
17. Cada ação gera o evento do outbox correspondente na mesma transação, com nome estável, organização, unidade, ids e valores em centavos.
18. Os contratos OpenAPI e o cliente TypeScript são regenerados e commitados; `oasdiff` não acusa quebra.
19. Os testes de domínio puro cobrem transições de estado, saldo, taxa, arredondamento, troco, janela de desfazer e numeração, sem banco nem relógio ambiente.
20. A tela `/salao` abre no celular (390x844 e 360x740) e completa, via Playwright contra a API real, o fluxo: entrar, abrir caixa, abrir comanda por teclado, lançar item, enviar rodada, desfazer, fechar a conta em dinheiro com troco, fechar caixa, sem rolagem horizontal e com alvos de toque de pelo menos 44 px.
21. Abrir comanda e lançar o primeiro item leva menos de 10 s e cada item seguinte no máximo 2 toques, medidos num roteiro Playwright com o garçom já logado e o caixa aberto (meta do `05` U1; confirmação em aparelho real fica como validação do dono, sem bloquear o ticket).
22. Com a rede cortada, é possível continuar montando a rodada e ver a fila; ao voltar, as ações são enviadas em ordem e nada duplica (Playwright com o contexto offline).
23. Todo estado da seção "Salão" do `DESIGN.md` (carregando, vazio, erro, sucesso, offline, conflito, sem permissão) existe e tem screenshot nos dois viewports; nenhuma das proibições do `frontend-craft` aparece.
24. Dois locatários rodam o fluxo completo (login real, caixa, comanda, pagamento, fechamento) sem vazamento, e a varredura das tabelas continua em zero linhas cruzadas.
25. `dotnet build` sem avisos, `dotnet format --verify-no-changes` limpo, `pnpm -r typecheck` verde, e `dotnet test backend/Flow4Tech.sln` e `pnpm -r --if-present test` passam com Docker e PostgreSQL reais (Testcontainers); o CI do GitHub roda todos os portões sobre a branch pela primeira vez.
26. `CLAUDE.md` é atualizado com as seções dos módulos novos, e as ADR-014 e ADR-015 estão escritas e aprovadas.

## Fora de escopo
- Fase 2: cortesia, brinde, desconto, couvert, transferir, juntar, dividir conta, reabrir comanda, cancelar comanda, estorno, gorjeta, fiado, voucher, outros métodos de pagamento (Pix, crédito, débito, vale), preço promocional, opções e adicionais, ficha técnica, combos, estoque, impressão e comprovante, cozinha e KDS, reservas, clientes, relatórios.
- Caixa completo (2.10): cadastro de caixas físicos, suprimento, sangria, conferência cega, aprovação de diferença e tolerância, fechamento do dia operacional, reabertura, agenda de funcionamento e bloqueio por horário (2.10b).
- Itens 1.9 (notificações e SSE), 1.13 (Control Tower) e 1.14 (observabilidade) da própria Fase 1B: cada um terá a sua spec; a central de comandas na web (2.5) e o mapa de mesas em tempo real também.
- Rotas HTTP para gerir perfis e atribuir permissões (a 1A deixou isso pendente): os testes criam perfis e atribuições pelo serviço `GestaoDePerfis` e por semeadura, e a tela de usuários e perfis é da Fase 2 (decisão do dono, ver Notas).
- Convite, recuperação de acesso e login com Google na tela do celular (as rotas existem; a UI delas não faz parte da fatia: só entrar com usuário operacional ou e-mail e senha, trocar a senha temporária e escolher a unidade).
- App nativo, impressão Bluetooth, leitura de código de barras, ditado de voz e offline completo (D7).
- Enviar e-mail real, CAPTCHA real e agendamento do despachante do outbox (continuam pendentes da 1A; esta fatia só grava eventos).
- Identidade visual final (v6 ou v7), logotipo e nome do produto; os tokens do protótipo valem como provisórios.

## Notas
- **Perguntas respondidas pelo dono (2026-11-05):**
  1. *Perfis:* fora da fatia; os testes semeiam perfis pelo serviço `GestaoDePerfis`. Para o piloto da Fase 2 ainda é preciso decidir entre rotas de perfis ou perfis de fábrica "garçom" e "caixa" (pendência registrada para a Fase 2).
  2. *Caixa:* turno por operador, sem cadastro de caixas, serve para a demonstração.
  3. *Taxa de serviço:* padrão 10% ligado.
  4. *Referências visuais:* desenho autoral para entrar, caixa e lista de comandas; o dono aprova olhando os screenshots do primeiro ticket de UI (`approved: pending` no `DESIGN.md` até lá). Se tiver imagens, colocar em `docs/design/refs/salao/`.
  5. *Seam de UI:* confirmado, com o Playwright contra a API real em `docker compose`.
- **Capturas do protótipo que faltaram** (lista de comandas, vazio, esqueleto, erro): a navegação por aba não é endereçável por hash no protótipo; recapturar com Playwright clicando no botão da barra inferior ou abrir o protótipo no navegador (`index.html#m=mobile`). `13-fila.png` não foi conferida visualmente.
- **Pendências herdadas da 1A que esta fatia pisa:** o CI do GitHub nunca rodou; as suítes de PostgreSQL dos tickets 04 a 11 não foram todas executadas; os tickets 04 a 11 não têm revisão independente; o despachante do outbox não está no DI nem agendado; o resolvedor do dia operacional não lê as opções `dia-operacional.*`. Rodar o CI e as suítes completas antes do primeiro ticket desta spec reduz o risco.
- **Riscos de desenho a observar:** o bloqueio de numeração (contador por unidade e dia sob trava) e a cadeia de auditoria por organização (trava de advisory) são pontos de serialização no pico; manter as unidades de trabalho curtas e medir com carga simulada na Fase 2. Dois aparelhos refrescando a sessão ao mesmo tempo matam a sessão (regra do refresh rotativo): o app deve renovar de um único lugar.
- **Ordem sugerida dos tickets** (para o `/tickets`): ADR-014 e esqueleto dos módulos; cardápio mínimo; mesas; caixa (abrir, fechar); comanda (abrir, numeração, rodadas, desfazer, cancelar item); pagamento em dinheiro e fechar; opções em camadas e permissões; contratos OpenAPI e cliente; esqueleto do app `/salao` com entrar e unidade; telas de comanda e rodada; tela de fechar conta e caixa; fila de ações e offline; revisão visual com screenshots; fechamento com dois locatários.
- Roadmap: com esta spec e o 1.13/1.14/1.9, a Fase 1 fecha; o critério de saída da Fase 1 exige a fatia em `staging` no celular com dois tenants de teste.
