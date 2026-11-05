# Handoff: spec do domínio do salão (Fase 1B)

**Atualização posterior (mesma data):** os itens 2 e 3 de "O que falta" foram feitos como rascunho: `docs/design/DESIGN.md` (base + seção "Salão (mobile slice)", modo `--auto`, `approved: pending`) e `.scratch/dominio-salao/spec.md`. O seam de UI foi escrito como "proposto, aguardando confirmação do dono". Faltam: o dono revisar a spec e responder as 5 perguntas em aberto (seção Notas), e então `/tickets`. O restante deste arquivo descreve o estado de antes desses dois arquivos.

Escrito em 2026-11-05. Branch: `claude/flow4tech-fase-1-handoff-vgu3m9` (parte da Fase 1A fechada, commit `0103b56`). Nada foi enviado ao GitHub por esta sessão.

## Onde estamos no projeto

- **Fase 0 (`fundacao-tecnica`)**: feita, falta o ticket 12 (`pending`).
- **Fase 1A (`plataforma-base`)**: 11 tickets `done-local`, só backend. Os tickets 04 a 11 não têm revisão independente; o CI do GitHub nunca rodou; parte dos testes de PostgreSQL não rodou. Detalhes em `.scratch/plataforma-base/state.md`.
- **Fase 1B**: não começou. Itens do roadmap: 1.9 (notificações/SSE), 1.12 (fatia `/salao`), 1.13 (Control Tower mínima), 1.14 (observabilidade). Pré-requisito: spec do modelo de domínio (comanda, item, pagamento, caixa, mesa, cliente). **Essa spec ainda não existe.**
- **Frontend**: `frontend/{app,control,design-system}` só têm `src/index.ts` vazio.
- **Branches**: `refactor-v2` ⊂ `fundacao-tecnica` ⊂ `claude/flow4tech-fase-1-handoff-vgu3m9` (linha reta, fast-forward possível). `main` está 306 commits à frente (sistema antigo). Recomendado: PR da handoff para `refactor-v2` para o CI rodar pela primeira vez. Ainda não feito.

## O que eu estava fazendo

Rodando `/spec` para o domínio do salão + fatia `/salao`. Passos do `/spec`: 1 Ground, 2 Seams, 3 Screens/DESIGN.md, 4 Write.

### Decisões já tomadas pelo dono (nesta sessão)

1. **Escopo: backend + tela `/salao`** (celular do garçom). Como não existe `docs/design/DESIGN.md`, rodar o `/brief` inline **baseado no protótipo** (`.scratch/novo-sistema/prototipos/index.html`, modo `#m=mobile`).
2. **Cardápio mínimo agora**: módulo `cardapio` só com produto (nome, preço em centavos, ativo) e categoria simples; o item da comanda congela o preço. Fase 2 amplia.
3. **Seams confirmados**: (1) HTTP com PostgreSQL real, no estilo `DoisTenantsEndpointsTests` (login, abrir caixa, abrir comanda, lançar, pagar em dinheiro, fechar, permissão, auditoria, outbox, isolamento entre organizações); (2) testes puros de Domínio (estados da comanda, saldo, arredondamento em centavos). **Falta confirmar o seam de UI** (a tela entrou depois): proposta é vitest + testing-library para lógica/estado/componentes e Playwright (screenshots 390x844) para os fluxos críticos.
4. **Pagamento na fatia: só dinheiro, com troco.** Estorno, divisão da conta, Pix/crédito/débito/vale ficam para a Fase 2.8. Manter a interface de provedor "manual" (F4) para TEF/Pix depois.

### Já feito

- Leitura de `02-features.md` (F1 Comandas, F4 Pagamentos, F5 Caixa, F16 Clientes), `05-ux-design.md` (U1, U2, U4, U7, U18, U8), `06-roadmap.md` (R2, R3, R4, R5), `04-arquitetura.md` (módulos, A11 frontend), `docs/agents/*`, `backend/modules.dependencies.json` (hoje: Organizacao, Permissoes, Identidade).
- Li `~/.claude/skills/brief/SKILL.md` e carreguei `frontend-craft`.
- Gerei imagens de referência do protótipo em `docs/design/refs/salao/` (390x844):
  - `01.png` home "Agora" (fora da fatia; serve só para tokens)
  - `02.png` comanda Mesa 12 (itens em cima, cardápio embaixo, barra "Repetir rodada / Fechar conta")
  - `03.png` folha "Enviar rodada" (desfazer por 8 s)
  - `04.png` teclado numérico "Nova comanda"
  - `05.png` folha "Fechar conta" (tem Pix/crédito/etc.: **fora do escopo**, só dinheiro)
  - `07.png` comanda com rodada pendente, tema escuro
  - `12-offline.png` faixa "Sem conexão. Nada se perde."
  - `13-fila.png` não conferi visualmente
- **Não consegui capturar** a lista de comandas (aba Comandas), nem estados vazio/esqueleto/erro: o clique na aba falhou no script. Refazer com Playwright clicando no botão certo, ou abrir o protótipo no navegador.

## O que falta fazer

1. **Confirmar o seam de UI** com o dono (item 3 acima).
2. **Rodar o `/brief` (modo `--auto`)** e criar `docs/design/DESIGN.md` (inglês): base (register `product`, direção Minimalist com toques de Technical, intensidade `internal`, stack React + Vite + TanStack + Tailwind + Radix, vitest + testing-library + Playwright, viewports 390x844 e 360x740) + seção "Salão (mobile)".
   - Tokens vindos do protótipo (hex a converter para OKLCH): fundo `#F8F8F9`, card `#FFFFFF`, tinta `#0F172A`, muted `#5B6678`, marca azul `#3367D6`, aberta `#2B7A62`, aguardando `#C48A1D` (texto `#85570F`), alerta `#BE3F55`; escuro: fundo `#0F1115`, card `#171A1F`, marca `#6AA8FF`; raios 14/16/12/20 (folha); Geist + algarismos tabulares; alvos de toque 44 a 52 px.
   - Atenção: `--faint #8A93A3` não passa 4,5:1 sobre o fundo; não usar para texto essencial. A "bolinha de configuração" é ferramenta do protótipo, não vai para o produto. A tela "Agora" (métricas em cartões) é o padrão "hero-metric template" banido; fora da fatia.
   - Conflitos a registrar: folha de fechar conta do protótipo oferece vários métodos (fatia = só dinheiro); abas Agora/Calendário/Avisos/Mais fora da fatia.
   - **Sem referência no protótipo** (dizer ao dono e pedir imagens ou aceitar "autoral"): login real (o protótipo tem só "Entrar como…"), caixa no celular (U18 diz "Em breve" no celular), troca/seleção de unidade, tela de troco/dinheiro recebido, lista de comandas.
   - Assinatura sugerida: barra fixa de rodada ("5 itens não enviados · Enviar") com desfazer, mais a fila visível de ações pendentes.
   - Explorado e descartado: wizard em passos (abrir, mesa, itens, enviar) por exceder a meta de 2 toques.
3. **Escrever `.scratch/dominio-salao/spec.md`** (português) com as seções do `/spec`: Problema, Solução, User stories, Decisões de implementação, UI Contract, Seams, Decisões de teste, Critérios de aceite, Fora de escopo, Notas. Pontos de domínio a decidir/registrar:
   - Módulos novos e arestas em `backend/modules.dependencies.json`: `cardapio`, `comandas`, `pagamentos`, `caixa` (arquitetura: comandas → cardapio, pagamentos; ninguém → comandas, só por eventos). Decidir se mesa vive em `comandas` ou módulo próprio.
   - Comanda: estados `aberta → fechada | cancelada`, número de exibição #N por unidade (reinicia por dia operacional), mesa opcional (status derivado, não guardado), versão para concorrência otimista, item com preço congelado e estados `lançado/cancelado`, rodada enviada com janela de desfazer.
   - Dinheiro e troco em centavos (`Money`); pagamento imutável; saldo zero para fechar.
   - Caixa: turno, abertura com fundo de troco, fechamento com contagem (sem aprovação de diferença na fatia, a decidir), exigir caixa aberto para receber.
   - Permissões do catálogo F1/F4/F5 que a fatia usa; auditoria por ação; eventos de outbox (`comanda.*`, `pagamento.*`, `caixa.*`); migrações SQL com RLS e composite FKs; dia operacional (o resolver hoje não lê as configurações, roadmap 2.10b).
   - Fora de escopo explícito: SSE/notificações (1.9), Control Tower (1.13), observabilidade (1.14), cozinha/KDS, estoque, cortesia/brinde/desconto, transferir/juntar/dividir, reservas, clientes.
4. Depois da spec: `/tickets` para quebrar em `.scratch/dominio-salao/issues/NN-*.md`.
5. Em paralelo, independente: abrir o PR da handoff para `refactor-v2` e deixar o CI rodar; rodar `dotnet test` com Docker.

## Restrições do ambiente que importam

- `/spec`, `/to-spec`, `/brief` só rodam por comando do usuário (`disable-model-invocation`), mas o texto do `/spec` manda seguir o `/brief` inline; foi o que eu estava fazendo.
- Ao retomar, a pergunta de branch (`propose_branch`) foi recusada pelo dono: escolher a branch antes de editar (ou perguntar).
- O classificador de comandos falhou várias vezes de forma transitória: repetir o comando.
- Nada foi enviado ao GitHub por esta sessão.
