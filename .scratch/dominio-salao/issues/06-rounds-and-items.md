# 06: Rounds and items: launch, undo, cancel

**What to build:** A waiter launches a round of items (quantity, note) in one send, with product name and price frozen; can undo the whole round within 8 seconds; can cancel an item with a mandatory reason. Repeating the same round id does not duplicate. Two devices launching into the same comanda lose nothing.
**Blocked by:** 02, 05
**Touches:** backend/src/Comandas/**, backend/src/Api/** (routes), backend/migrations/0019_*.sql, backend/tests/Flow4Tech.Tests/Rodadas*Endpoints*.cs, domain tests, permissions/audit/settings catalogs, openapi.json, schema.ts
**Nature:** objective
**tdd-scope:** required
**Seams:** HTTP with real PostgreSQL; Pure domain; Architecture gates
**Status:** ready-for-agent

- [ ] Migration adds `rodadas` (client-generated id, comanda, sent_at, undone_at) and `itens_comanda` (product, frozen name and price, positive integer quantity, note, state lancado|cancelado, cancel reason, who cancelled), tenant tables with RLS.
- [ ] Launching copies product name and price into the item; changing the product afterwards does not change the comanda; an inactive product is refused; launching into a non-open comanda is refused.
- [ ] Repeating the same round id 1 to 5 times produces one round and one set of items and the same response.
- [ ] Undo cancels the whole round only within `comanda.janela-desfazer-segundos` (system-only setting, default 8, measured by `IClock`), only by the user who sent it, and is refused afterwards; its cancel reason is fixed ('desfeito pelo garçom').
- [ ] Cancel item requires a reason from the standard list (cliente desistiu, erro de lançamento, item indisponível, outro) with free text when 'outro'; the cancelled item leaves the consumption; the reason is in the audit record.
- [ ] Two clients launching into the same comanda concurrently lose no item; a stale `versao` returns 409 with the current version and changes nothing (real PostgreSQL test).
- [ ] Audit records `comanda:lancar-rodada`, `comanda:desfazer-rodada`, `comanda:cancelar-item` (cancel and undo are sensitive routes) and events `comanda.rodada-enviada`, `comanda.rodada-desfeita`, `comanda.item-cancelado` in the same unit of work; permissions `comanda:lancar-item`, `comanda:cancelar-item`, `comanda:desfazer-rodada` declared; contract and catalogs regenerated.
