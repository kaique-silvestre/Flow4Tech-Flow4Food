# F4-0057: Comanda edit and history screens

**What to build:** From the comanda screen a permitted user edits an item, renames the comanda, changes the waiter and people count, reads the history and cancels the comanda.
**Blocked by:** F4-0056, F4-0045
**Touches:** frontend/app/src/features/comandas/**, frontend/design-system/**, frontend/app/e2e/**
**Nature:** ui
**tdd-scope:** required
**Seams:** Browser (Playwright against the real API in `docker compose`, 390x844 and 360x740) plus vitest/testing-library for logic, state and components.
**Status:** done

- [x] Item action sheet gains 'Alterar'; comanda menu gains rename, waiter, people, note, history, cancel (each shown only with its permission).
- [x] History as a timeline list; cancel asks the standard reason and confirms before sending.
- [x] Edits go through the queue except cancel comanda, which needs a connection (stated in the sheet).
- [x] Screenshots at both viewports, light/dark; states loading, empty history, error, conflict 409.

## Decisões e desvios (F4-0057)
- Rota nova `GET /comandas/garcons` (backend, não estava nos "Touches"): a tela de trocar garçom precisa listar a equipe e `/contas` exige `perfil:ver`, que o Gerente modelo não tem.
- Estados na captura de tela: histórico, item, menu, cancelar (motivo e confirmação) e conflito 409 em claro e escuro, nos dois tamanhos. Carregando, vazio e erro do histórico/equipe ficam nos testes de componente (o histórico real nunca é vazio: a abertura já é um evento).
- O teste `asks the server to search...` de `ComandaPage.test.tsx` era intermitente (lia a primeira lista que aparecia); passou a esperar a lista final.

