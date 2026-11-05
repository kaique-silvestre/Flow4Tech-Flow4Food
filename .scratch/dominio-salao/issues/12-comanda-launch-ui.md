# 12: Screen: comanda, launch items, send round, undo, cancel item

**What to build:** The comanda screen: bill on top, picker below (search, category tabs, frequent items with ×N and +, last-time suggestion, Repeat round), staged round with steppers, the round bar, the Send round sheet and the 8-second undo, and cancel item with a reason.
**Blocked by:** 06, 11
**Touches:** frontend/app/src/features/comandas/**, frontend/design-system/** (round bar, item rows, sheet), e2e specs
**Nature:** ui
**tdd-scope:** required
**Seams:** UI in the browser
**Status:** ready-for-agent

- [ ] Layout follows `02.png`: top bar (back, breadcrumb, running total, state badge with elapsed time), bill rows, search, category tabs (Frequentes first), suggestion card, product rows with ×N and +, bottom bar 'Repetir rodada' + 'Fechar conta'.
- [ ] A product adds in 2 taps (+ then confirm in the round bar) or 1 tap for the next unit; staged items show steppers and the round bar `N itens não enviados · R$ x` with the accent 'Enviar'; focus ring never clipped by a sheet.
- [ ] Send opens the sheet as in `03.png`; after sending a toast with 'Desfazer' counts 8 seconds (linear bar; reduced motion respected); undo cancels the round; after the window the action is gone.
- [ ] Cancel item asks for a reason from the standard list ('outro' asks text); the item leaves the bill.
- [ ] Dark theme matches `07.png`; states: loading skeleton, search with no results ('Nada com "xyz". Tente o código do produto.'), error with retry, send failure keeps the round staged and shows what did not go, no permission (action absent).
- [ ] Playwright against the real API: from the list open a comanda, add items, send, undo within the window, send again, cancel an item; each critical state has screenshots at 390x844 and 360x740; no horizontal scroll; targets at least 44px.
- [ ] Timing: from the signed-in list to the first item launched in under 10 seconds scripted; each next item at most 2 taps.
