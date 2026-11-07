# 016. Fluxo de branches e IDs de ticket

- Status: proposed (aguarda decisão do dono)
- Date: 2026-11-06

## Context
O repositório tem branches de sessão (`claude/...`), planejamento (`refactor-v2`), fundação (`fundacao-tecnica`) e o sistema antigo (`development`, `staging`, `main`, outra linhagem, sem ancestral comum). Os nomes não dizem o que cada branch contém, e os tickets só são numerados dentro de cada fatia (`.scratch/<fatia>/issues/NN-...`), então "ticket 13" é ambíguo. Queremos que tudo que se desenvolve seja rastreável: ticket -> branch -> commits -> PR -> `dev-v2` -> `main-v2`.

## Decision (proposta)
**Branches de longa vida (sistema novo)**
- `main-v2`: o que foi revisado e pode ser publicado. Protegida: só recebe PR vindo de `dev-v2`, com o job `gates` verde.
- `dev-v2`: integração. Aceita commit e push direto (emenda de 2026-11-07); feature maior entra por PR com `gates` verde. O CI roda em todo push.
- `main`, `staging`, `development` e `refactor-v2` ficam como estão (sistema antigo e planejamento); não recebem trabalho novo.

**ID de ticket:** `F4-NNNN`, um número sequencial único no repositório (F4 = Flow4Tech), sem área no nome, nunca reaproveitado. O ID só identifica; ele não carrega fase, PR nem spec, porque isso muda. Tudo o que liga o ticket ao resto vive no índice `docs/tickets.md` (ID, título, fatia, spec, arquivo do ticket, branch, PR, status), atualizado a cada ticket aberto e a cada PR. Os 38 tickets já feitos (fundação, plataforma base, salão) receberam `F4-0001` a `F4-0038` em ordem cronológica.

**ID de fatia (emenda de 2026-11-07, F4-0075):** cada fatia de trabalho (`.scratch/<fatia>/`) tem um ID `S-NN` sequencial, único e nunca reaproveitado, e a pasta se chama `S-NN-<nome>`. Assim a ordem e a época de cada fatia aparecem no próprio nome. A tabela "Fatias" de `docs/tickets.md` guarda abertura, fechamento e faixa de tickets. Fatias existentes: `S-00-novo-sistema`, `S-01-fundacao-tecnica`, `S-02-plataforma-base`, `S-03-dominio-salao`, `S-04-salao-rapido`. Trabalho transversal fica no trilho permanente `.scratch/governanca/`, sem `S-NN`.

**Nome de branch de trabalho:** `<tipo>/F4-NNNN-<resumo-em-kebab>`, a partir de `dev-v2`. Tipos: `feat`, `fix`, `refactor`, `test`, `docs`, `chore`. Exemplo: `feat/F4-0013-fila-offline`. A barra agrupa branches no Git; parênteses ficam só em commit e título de PR. Uma branch por ticket, vida curta (dias). Manutenção sem ticket de produto abre um ticket antes (mesmo contador).

**Commits e PRs:** commit `tipo(F4-NNNN): descrição` (Conventional Commits), por exemplo `feat(F4-0013): fila de ações pendentes`. Título do PR igual ao do commit principal, e o corpo cita o ID, o arquivo do ticket e o que foi verificado. Merge de feature em `dev-v2` por squash (um commit por ticket, com o ID no título); `dev-v2` -> `main-v2` por merge commit (preserva a lista de tickets de cada entrega).

**Rastreio:** `git log --grep "F4-0013"` ou a busca de PRs por `F4-0013` devolve tudo de um ticket; `git branch -a | grep F4-0013` acha a branch; o índice mostra o resto. Um check no CI (a decidir) reprova PR cuja branch ou título não tenha ID.

**Branches de sessão (`claude/...`):** são temporárias. O trabalho sai delas por PR para `dev-v2` e elas são apagadas depois do merge.

## Consequences
- Mais cerimônia por mudança (abrir ticket, nomear a branch), em troca de rastreio completo.
- Trabalho já feito (plataforma base e salão) entra em `dev-v2` num PR único de legado, listando `F4-0001` a `F4-0038`. Commits antigos não são reescritos (mudaria os hashes e quebraria referências); o índice faz o mapeamento, e só os commits novos seguem o padrão.
- Proteção de branch e o check de nome dependem de configuração no GitHub (Settings > Branches ou rulesets), que é decisão do dono.

## Emenda 2026-11-07 (F4-0077): CI e ambientes
- Sem branches `release`, `teste` ou `prod`. Ambiente é do Railway, não da branch.
- O CI (`ci.yml`, job `gates`) roda em todo PR e em todo push em `dev-v2`, `main-v2` e `main`. Push direto em `dev-v2` é permitido, mas não fica sem verificação: quem empurra confere o resultado, e um `gates` vermelho em `dev-v2` é corrigido antes de qualquer outro trabalho.
- `main-v2` só recebe PR de `dev-v2`; o CI roda no PR e de novo no merge commit.
- Deploy do sistema novo ainda não existe. Quando existir (projeto Railway próprio, ver `docs/runbooks/deploy.md`): `dev-v2` verde -> `staging` automático; `main-v2` verde -> `production` manual com aprovação. O deploy sai do commit exato que o CI validou.
- Classes de teste que sobem banco devem ter `Endpoints` ou `Integration` no nome (o CI separa por nome); `ClassificacaoDeTestesTests` falha se não tiverem (F4-0076).
