# Gestão na web e app para várias telas (Fase 2, Marco 2B)

Fatia: S-05
Aberta: 2026-11-15
Fechada: em andamento
Tickets: a registrar em `docs/tickets.md` quando cada um abrir (próximo livre ao abrir)

Status: aprovada pelo dono em 2026-11-15 (rascunho 2), escrito pelo assistente a partir do Marco 2B da `S-04-salao-rapido/spec.md` (B1 a B9), de `docs/design/DESIGN.md`, de `01-visao.md` D17 e do protótipo `.scratch/S-00-novo-sistema/prototipos/index.html` (modos `#m=mobile` e `#m=web`). Itens *(proposta)* são decisões a confirmar pelo dono. Os pontos de teste da seção Seams foram apresentados ao dono em 2026-11-15 e não foram contestados; confirmar na aprovação.

## Problema
O 2A entrega o salão só para celular. O DESIGN.md diz que acima de 480 px a coluna do celular fica centralizada e que a gestão na web "assume", mas essa parte não existe. A visão (D17) promete celular, tablet e computador numa interface adaptável, sem versão separada. Ao mesmo tempo, o app de staging está mais pobre que o protótipo (cabeçalho com empresa, unidade e turno, filtros, valor e garçom nos cartões, menu com ícones). A gestão do restaurante (central de comandas, promoções, clientes, estorno, agenda, reservas) é trabalho de computador e tablet, e não dá para construí-la antes de a base de várias telas existir.

## Solução
Dois blocos, nesta ordem, depois do piloto em modo sombra do 2A:
1. **Bloco R, base para várias telas.** O app todo (salão do 2A incluído) passa a funcionar em celular, tablet e computador com o mesmo conteúdo e a mesma função, e a lista de comandas e o shell chegam ao nível do protótipo. Nenhuma tela do bloco G começa antes do R terminar.
2. **Bloco G, gestão na web.** Central de comandas ao vivo, estorno, clientes e fiado, promoções, agenda, reservas, edição de permissões, já nascendo adaptável.

A gestão mora no `frontend/app`, rota `/gestao` *(proposta)*. O `frontend/control` fica reservado para a Control Tower (Fase 6; identidade separada, ADR-005).

Referência visual, para cada tela daqui em diante: o protótipo, nos dois modos. O modo celular (`#m=mobile`) vale para o garçom; o modo web (`#m=web`) vale para computador e, adaptado, para tablet.

## User stories
**Base para várias telas (R)**
1. Como garçom, quero abrir o mesmo app no celular, no tablet do restaurante e no computador do caixa, para não aprender três sistemas.
2. Como garçom, quero ver no cabeçalho a empresa, a unidade, o turno e quem está logado, para saber em que contexto estou lançando.
3. Como garçom, quero filtrar a lista de comandas (minhas, todas, abertas, aguardando) com contagem, para achar a minha mesa rápido.
4. Como garçom, quero ver em cada comanda o valor, o tempo aberta, o garçom e o alerta de comanda parada, para decidir a quem atender primeiro.
5. Como garçom, quero um menu de baixo com ícones e rótulos, para me localizar sem ler.
6. Como caixa, quero no tablet a lista de comandas e a comanda aberta lado a lado, para fechar contas sem ir e voltar entre telas.
7. Como caixa, quero no computador usar o teclado (N para nova comanda, digitar "3 chope" e Enter para lançar, Ctrl+K para buscar, Esc para fechar, `[` para recolher o menu), para lançar mais rápido que com o mouse.
8. Como pessoa com deficiência visual ou motora, quero foco visível, ordem de tabulação correta e contraste nas três faixas, para usar o app em qualquer tela.
9. Como dono, quero que o tema claro e escuro funcione igual nas três faixas, para o restaurante escolher.
10. Como equipe, quero que as folhas do celular virem painéis ou janelas em tela grande sem mudar a regra, para não duplicar lógica.

**Gestão na web (G)**
11. Como gerente, quero uma central ao vivo de comandas (lista, tabela e mapa) com filtro por garçom, mesa, tempo aberto e número, para enxergar o salão inteiro.
12. Como gerente, quero ver a mudança de uma comanda feita no celular do garçom na central em poucos segundos, sem recarregar.
13. Como gerente, quero editar uma comanda e forçar o fechamento, dentro da minha permissão, para destravar uma mesa.
14. Como gerente, quero estornar um pagamento, total ou parcial, com motivo, voltando ao turno certo e dentro do meu limite, para corrigir um erro do caixa.
15. Como gerente, quero cancelar uma comanda que já tem pagamento, para tratar casos reais sem mexer no banco.
16. Como atendente, quero cadastrar e buscar clientes com consentimento e aviso de duplicidade, e vincular o cliente à comanda de forma opcional.
17. Como dono, quero que os dados pessoais do cliente fiquem mascarados para quem não tem a permissão, para cumprir a LGPD.
18. Como dono, quero vender fiado e emitir voucher com saldo por cliente, para fidelizar sem planilha.
19. Como gerente, quero criar promoções e combos pontuais ou recorrentes, por horário e unidade, aplicados sozinhos e registrados no item, para não dar desconto na mão.
20. Como dono, quero definir a agenda de funcionamento (semana padrão, exceções, "estender hoje") e que o sistema bloqueie lançamentos fora do horário, para o dia operacional ficar certo.
21. Como atendente, quero reservar uma mesa e marcar "chegou", transformando a reserva em comanda.
22. Como dono, quero editar quais permissões cada perfil tem, com os modelos como ponto de partida, para ajustar ao meu restaurante.
23. Como dono, quero medir o sistema com carga simulada e aprovar a Fase 2, para saber que está pronto para uso real.

## Decisões de implementação
- **Faixas de tela.** Celular, tablet e computador. Os limites vêm do protótipo, que já tem regras de largura (celular abaixo de 500 px, compactação do modo web entre 1000 e 1280 px), e só são fixados no ADR do R1. Os valores 600 e 1024 usados no rascunho 1 são provisórios; o dono decidiu "uso comercial, sem aparelho de referência", ou seja, padrão de mercado.
- **Uma interface, sem versão separada.** O mesmo código serve as três faixas. O shell escolhe a navegação por faixa: barra de abas embaixo no celular, trilho no tablet, barra lateral (como o protótipo) no computador, sempre filtrada pelo motor de autorização.
- **Cabeçalho de contexto** com empresa, unidade, turno, usuário e troca de tema, lido da sessão (organização e unidade nunca vêm do cliente).
- **Folhas viram painéis.** Os sheets do celular passam a painel lateral ou janela em tablet e computador; a lógica e as rotas são as mesmas.
- **Lista de comandas ao nível do protótipo.** Os dados de valor, garçom, tempo aberta e "parada há N min" vêm da lista que o backend já devolve ou de uma extensão dela, decidida no ticket (sem consulta fora da unidade de trabalho).
- **Teclado no computador** como no protótipo: N, "3 chope"+Enter, Ctrl+K, Esc, `[`. Só atua na tela de comandas.
- **Tempo real (SSE).** Um fluxo autenticado por sessão entrega os eventos do outbox da organização e unidade da sessão; a versão da comanda é a fonte de ordem; o cliente recarrega por versão quando perde um evento. Pré-requisito P3 da S-04: despachante do outbox e agendamento ligados em produção.
- **Estorno.** Novo comando no módulo de Pagamentos; gera lançamento negativo imutável (nunca edita o pagamento), exige motivo, volta ao turno do caixa certo, e o limite é decidido pelo motor de autorização no handler com valores do servidor (como o desconto, ADR-018). Cancelar comanda com pagamento passa a exigir estorno prévio ou estorno junto.
- **Clientes, fiado e voucher.** Módulo novo de Clientes (ADR a escrever): cadastro com consentimento, detecção de duplicidade, mascaramento por permissão `cliente:ver-dados-pessoais`; fiado e voucher são métodos de pagamento hoje desativados (A9 do 2A) que passam a ser habilitados com saldo por cliente.
- **Promoções e combos.** Regra persistida, avaliada no servidor na hora do lançamento; o item guarda a promoção aplicada; coexistência com desconto manual segue a ordem de cálculo do ADR-018.
- **Agenda de funcionamento.** O resolvedor do dia operacional passa a ler `dia-operacional.*` das configurações em camadas (ADR-013).
- **Reservas.** Estado "Reservada" no ciclo da comanda e transição "Chegou".
- **Edição de permissões do perfil.** Telas e rotas para o dono alterar as regras persistidas (ADR-006), com a matriz perfil x permissão do protótipo; toda alteração audita e passa pelo GrantGuard.
- **Auditoria e isolamento.** Toda rota nova declara sessão e permissão, audita na mesma unidade de trabalho e é coberta por teste com duas organizações.
- **Gate de arquitetura do front.** A gestão fica em `frontend/app` sem violar as regras do dependency-cruiser; o design system ganha os componentes de layout adaptável.

## UI Contract
- **registro:** product (DESIGN.md), direção minimalista com toques técnicos.
- **referência:** o protótipo `index.html`, modos `#m=mobile` e `#m=web`; os parâmetros de hash estão no cabeçalho do arquivo.
- **telas:**

| Tela | Rota | Referência no protótipo | Estados obrigatórios | Fluxos críticos |
|---|---|---|---|---|
| Shell adaptável | todas | `#m=mobile` (abas) e `#m=web` (barra lateral) | vazio, erro, sem conexão | trocar de aba por permissão; recolher menu |
| Comandas (lista) | `/salao` | `#m=mobile` e `#m=web&view=table` / cartões | vazio, esqueleto, erro, offline | filtrar, buscar, nova comanda |
| Comanda (detalhe) | `/salao/comandas/:id` | `#m=web&open=3`, `#m=mobile&c=12` | vazio, não enviado, fila offline | lançar, enviar, receber |
| Mesas | `/salao/mesas` | `#m=web&screen=mesas&mesa=12` | vazio, erro | abrir comanda na mesa |
| Caixa | `/salao/caixa` | `#m=web&screen=caixa&jtab=agora` | sem turno, diferença | abrir, sangria, fechar |
| Cardápio | `/salao/cardapio` | `#m=web&screen=cardapio` | vazio, erro | editar produto, preço, opções |
| Usuários e perfis | `/salao/pessoas`, `/gestao/perfis` | `#m=web&screen=usuarios`, `&screen=perfis&view=matriz` | sem permissão | convidar, atribuir, editar matriz |
| Central de comandas | `/gestao/comandas` | `#m=web` (lista, tabela) | vazio, erro, reconectando | filtrar, editar, forçar fechamento |
| Estorno | janela na central e no caixa | `#m=web` (janela grande) | acima do limite, negado | estornar total e parcial |
| Clientes e fiado | `/gestao/clientes` | `#m=web&screen=clientes&ltab=cli|fiado|dup` | mascarado, duplicado | cadastrar, vincular, fiar |
| Promoções | `/gestao/promocoes` | `#m=web&screen=promocoes` | vazio, conflito | criar, agendar, ativar |
| Agenda de funcionamento | `/gestao/agenda` | `#m=web&screen=configuracoes&ctab=op` | exceção, fora do horário | semana padrão, estender hoje |
| Reservas | `/gestao/reservas` | sem tela dedicada no protótipo: desenho autoral | vazio, conflito | reservar, "chegou" |

- **fora do DESIGN.md:** a seção "Várias telas e gestão na web" (faixas, navegação por faixa, painéis no lugar de sheets, atalhos de teclado, densidade) ainda não existe e precisa ser acrescentada ao `DESIGN.md` pelo `/brief` no R1. Telas do protótipo que pertencem a outras fases (estoque, compras, financeiro, equipe, relatórios, cozinha e bar, calendário, organização) ficam fora desta fatia.

## Seams
tdd-scope: required (backend e front); not-applicable para a spec e o ADR.

| Seam | O que se observa | Prior art |
|---|---|---|
| Navegador (Playwright, API real) | navegação e layout por faixa, paridade de função, folhas viram painéis, atalhos; capturas | `frontend/app/e2e/*.spec.ts`, projetos 390x844 e 360x740 em `playwright.config.ts`; acrescentar 820x1180 e 1440x900, nos dois temas |
| vitest e testing-library | lógica de faixa, shell filtrado por permissão, filtros da lista | `Shell.test.tsx`, `*.test.tsx` por feature |
| Duas organizações via HTTP | estorno, clientes, fiado, promoções, agenda, reservas, perfis, isolamento, permissões, auditoria, varredura de tabelas | `DoisTenantsEndpointsTests`, `*EndpointsTests` |
| Propriedades (FsCheck) | limite e soma do estorno, saldo do fiado, aplicação da promoção, janela de horário | testes de propriedade de F4-0105 |
| axe | acessibilidade nas três faixas | `acessibilidade.spec.ts` |
| Novo: fluxo SSE na borda HTTP | um cliente assina o fluxo; uma comanda mudada por outro aparece em ordem, só da própria organização e unidade, com PostgreSQL real; perda de evento recupera por versão | testes de outbox em `tests/Outbox`; seam proposto aqui por ser o que o usuário observa |

## Decisões de teste
- Um bom teste observa comportamento externo: o que o usuário vê numa faixa, o que a rota devolve, o que o fluxo entrega. Não testa componente interno nem consulta SQL.
- Layout: capturas e verificação de função em 390, 360, 820 e 1440, claro e escuro, comparadas com o protótipo pelo `design-reviewer`.
- Cada rota nova: caso permitido, negado e de outra organização (regra de rota segura do CLAUDE.md).
- Estorno e fiado têm testes de propriedade (somas, limites, nunca saldo negativo indevido).
- Tempo real: teste de ordem e de isolamento entre organizações, e de recuperação por versão.
- Cobertura do front e orçamento de bundle seguem os gates existentes; o bundle da gestão é dividido por rota.

## Critérios de aceite
1. A lista de comandas mostra cabeçalho de contexto, filtros com contagem, e em cada cartão valor, tempo, garçom e alerta de parada, no nível do protótipo.
2. O app inteiro funciona em 390, 360, 820 e 1440 px, nos dois temas, com paridade de função e sem corte de conteúdo.
3. Em tablet, lista e detalhe da comanda aparecem lado a lado; em computador, os atalhos N, "3 chope"+Enter, Ctrl+K, Esc e `[` funcionam.
4. Navegação por teclado e foco visível nas três faixas; axe sem violação nas três faixas.
5. Capturas de cada tela aprovadas pelo `design-reviewer` contra o protótipo (mobile e web).
6. Uma comanda mudada no celular aparece na central em até 3 segundos, sem recarregar, e uma organização nunca recebe evento de outra.
7. Estorno total e parcial com motivo, voltando ao turno certo, negado acima do limite do perfil, auditado.
8. Cliente cadastrado com consentimento; duplicidade avisada; dados pessoais mascarados sem a permissão; fiado e voucher com saldo correto.
9. Promoção aplicada sozinha no horário e na unidade certos, com a promoção registrada no item.
10. Lançamento fora do horário de funcionamento bloqueado e "estender hoje" funciona.
11. Reserva vira comanda ao marcar "chegou".
12. O dono edita as permissões de um perfil e a mudança passa pelo motor, com auditoria.
13. Medições com carga simulada registradas e aprovação do cliente sobre o fluxo.
14. Todos os gates do CI verdes (arquitetura do front, código morto, bundle, axe, mutação) e revisão independente de cada ticket sem BLOCK.

## Fora de escopo
Control Tower e cobrança (Fase 6); estoque, ficha técnica, compras e fornecedores (Fase 3); financeiro, relatórios, equipe e comissões (Fase 4); cozinha e bar, KDS, impressão e Bluetooth (Fase 9); NFC-e e TEF/Pix automático (Fase 10); calendário; app nativo; offline completo da gestão.

## Notas
- **Ordem de trabalho (decisão do dono, 2026-11-15):** F4-0072 fecha o 2A, o dono roda o piloto em modo sombra sozinho no celular, depois o S-05. Dentro do S-05: R1 a R5, depois G1, G2 (usa o R do tablet), G7, G3, G4, G5, G6, G8, G9, G10. G1 e G2 podem andar em paralelo com o fim do R se houver mais de um executor.
- **Alinhar a lista ao protótipo antes do piloto** foi proposto ao dono e ainda não virou ticket; se virar, ele entra como a primeira parte do R (história 2 a 5) e sai do S-05.
- **Tickets propostos** (cada um ganha ID `F4-NNNN` ao abrir): R1 ADR de faixas e DESIGN.md (inclui o `/brief`); R2 shell adaptável e cabeçalho; R3 salão em tablet; R4 salão em computador com teclado; R5 gates e capturas para as quatro resoluções; G1 SSE; G2 central de comandas; G3 estorno e cancelamento com pagamento; G4 clientes e LGPD; G5 fiado e vouchers; G6 promoções e combos; G7 agenda de funcionamento; G8 reservas; G9 edição de permissões; G10 fechamento da Fase 2.
- **Decisões do dono (2026-11-15):** G9 (edição de permissões do perfil) entra no 2B; a única identidade visual é a do protótipo `index.html` (F4-0133), então a pergunta v6 ou v7 deixa de existir.
- **Decisão do dono (2026-11-15):** a gestão desta fatia é a do restaurante e fica no `app` em `/gestao`; a gestão interna da Flow4Tech é a Control Tower (Fase 6). Sem perguntas abertas.
