# 016. Fluxo de branches e IDs de ticket

- Status: proposed (aguarda decisão do dono)
- Date: 2026-11-06

## Context
O repositório tem branches de sessão (`claude/...`), planejamento (`refactor-v2`), fundação (`fundacao-tecnica`) e o sistema antigo (`development`, `staging`, `main`, outra linhagem, sem ancestral comum). Os nomes não dizem o que cada branch contém, e os tickets só são numerados dentro de cada fatia (`.scratch/<fatia>/issues/NN-...`), então "ticket 13" é ambíguo. Queremos que tudo que se desenvolve seja rastreável: ticket -> branch -> commits -> PR -> `dev-v2` -> `main-v2`.

## Decision (proposta)
**Branches de longa vida (sistema novo)**
- `main-v2`: o que foi revisado e pode ser publicado. Protegida: só recebe PR vindo de `dev-v2`, com o job `gates` verde.
- `dev-v2`: integração. Toda feature entra aqui por PR. Protegida: PR obrigatório e `gates` verde.
- `main`, `staging`, `development` e `refactor-v2` ficam como estão (sistema antigo e planejamento); não recebem trabalho novo.

**ID de ticket, único no repositório:** `<ÁREA>-<NNN>`, com área em 3 letras e número de 3 dígitos, nunca reaproveitado. Áreas hoje: `FND` (fundação técnica), `PLT` (plataforma base), `SAL` (salão). Área nova entra na lista deste ADR. Os tickets já feitos ganham o ID retroativamente (salão 13 = `SAL-013`); o arquivo do ticket em `.scratch/<fatia>/issues/` passa a ter as linhas `ID:`, `Branch:` e `PR:`.

**Nome de branch de trabalho:** `<tipo>/<ID>-<resumo-em-kebab>`, a partir de `dev-v2`. Tipos: `feat`, `fix`, `refactor`, `test`, `docs`, `chore`. Exemplos: `feat/SAL-013-fila-offline`, `fix/SAL-014-chave-de-cache-do-caixa`. Uma branch por ticket; vida curta (dias, não semanas). Trabalho sem ticket (manutenção) usa `chore/MNT-NNN-...` com ticket de manutenção aberto antes.

**Commits e PRs:** commit `tipo(ID): descrição` (Conventional Commits), por exemplo `feat(SAL-013): fila de ações pendentes`. Título do PR igual ao do commit principal, e o corpo cita o ID, o arquivo do ticket e o que foi verificado. Merge de feature em `dev-v2` por squash (um commit por ticket, com o ID no título); `dev-v2` -> `main-v2` por merge commit (preserva a lista de tickets de cada entrega).

**Rastreio:** `git log --grep "SAL-013"` ou a busca de PRs por `SAL-013` devolve tudo de um ticket; `git branch -a | grep SAL-013` acha a branch. Um check no CI (a decidir) reprova PR cuja branch ou título não tenha ID.

**Branches de sessão (`claude/...`):** são temporárias. O trabalho sai delas por PR para `dev-v2` e elas são apagadas depois do merge.

## Consequences
- Mais cerimônia por mudança (abrir ticket, nomear a branch), em troca de rastreio completo.
- Trabalho já feito (plataforma base e salão) entra em `dev-v2` num PR único de legado, listando os IDs retroativos.
- Proteção de branch e o check de nome dependem de configuração no GitHub (Settings > Branches ou rulesets), que é decisão do dono.
