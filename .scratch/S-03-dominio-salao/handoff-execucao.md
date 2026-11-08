# Handoff de execução: fatia /salao (parado no ticket 05)

Escrito em 2026-11-05. Branch `claude/flow4tech-fase-1-handoff-vgu3m9`. Nada enviado ao GitHub por esta sessão. Este arquivo complementa `handoff.md` (contexto e decisões do dono, não reabrir).

## Como a execução rodou
`/orchestrator .scratch/S-03-dominio-salao` (modo `--auto`, sequencial, sem worktrees por ticket porque todos mexem nos catálogos de permissão/auditoria/configurações e no `openapi.json`). Estado em `.scratch/S-03-dominio-salao/state.md`. O orquestrador foi parado pelo dono durante o ticket 05.

## Estado dos tickets
| Ticket | Status | Commit |
|---|---|---|
| 01 módulos e ADRs (014/015) | feito | `47effea` |
| 02 cardápio mínimo | feito | `46ab960` |
| 03 turno de caixa | feito | `96479e7` |
| 04 mesas | feito | `3b7c2a9` |
| 05 abrir comanda e listar | **implementado, sem revisão/fechamento** | `d9e9571` |
| 06 a 15 | pendentes | n/a |

`state.md` ainda marca o 05 como `running, round 1`: o orquestrador parou antes do reviewer e do merge. O explorer do 06 já rodou (gerou as 21 linhas novas em `packets/F4-0029.md`, commitadas junto com este handoff).

## Riscos anotados (resolver nos tickets indicados)
- 02: sem teste de domínio puro, sem teste `ToQueryString`, corrida de unicidade vira 500 (`23505`), congelamento de preço só se prova no ticket 06.
- 03: campos ausentes no corpo do fechamento viram 0; falta teste de atomicidade no rollback. Corrigir no 07.
- 04: a suíte base teve 2-3 falhas intermitentes sob carga (health, porta, latência); passam isoladas.
- Herdado da 1A: CI nunca rodou; tickets 04-11 da 1A sem revisão independente; parte das suítes PostgreSQL não rodou.

## Como retomar
1. `git fetch && git pull --ff-only` (a sessão em nuvem precisa da branch empurrada; o dono decide o push).
2. Antes de seguir: rodar `dotnet test backend/Flow4Tech.sln` (precisa Docker; na nuvem sem Docker ver a nota "test without Docker" no `CLAUDE.md`) para confirmar que 01-05 estão verdes.
3. Revisar o 05: pedir ao `reviewer` o diff de `d9e9571` contra `issues/05-*.md` e `packets/F4-0028.md`; corrigir achados; só então marcar `done` no `state.md`.
4. `/orchestrator .scratch/S-03-dominio-salao --resume` (só o usuário digita). Ele deve tratar o 05 como já implementado; se refizer, avisar que o commit existe.
5. Seguir a ordem: 06 → 07 (backend), depois 08-15 (front). 08 não depende do backend e pode andar antes, se nada colidir nos arquivos.

## Cuidados
- Dois worktrees de agentes sobraram em `.claude/worktrees/agent-*` (ambos em `e29a9b2`, sem trabalho útil). Limpar com a ferramenta de worktrees, não à mão.
- Telas de login, caixa e lista sem referência visual: desenho autoral; dono aprova pelos screenshots do ticket 09. `DESIGN.md` está `approved: pending`.
- Commits terminam com `Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>`. Respostas em português.
