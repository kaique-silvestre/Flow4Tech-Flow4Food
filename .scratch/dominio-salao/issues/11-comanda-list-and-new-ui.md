# 11: Screens: comanda list and new comanda by keypad

**What to build:** The waiter lands on their open comandas and can open a new one with a big numeric keypad (table number, free-table chips, 'dar um nome'), reaching the comanda ready to launch in well under 10 seconds.
**Blocked by:** 04, 05, 09
**Touches:** frontend/app/src/features/comandas/**, frontend/design-system/** (keypad, sheet), e2e specs
**Nature:** ui
**tdd-scope:** required
**Seams:** UI in the browser
**Status:** ready-for-agent

- [ ] `/salao` lists open comandas (display #N, identification, status badge with marker and word, elapsed time) with search; the waiter sees only their own unless allowed; `Nova comanda` is the single accent action.
- [ ] New comanda opens a bottom sheet with the keypad as in `04.png`: huge number entry, free-table chips (when tables are on), Apagar/0/Cancelar, 'Dar um nome', and OK disabled until there is a value; with tables off the field reads 'Comanda 7'.
- [ ] A table already occupied shows the existing comanda and offers to open another; opening lands on the comanda screen.
- [ ] States: loading skeleton, empty ('Nenhuma comanda aberta. Toque em Nova comanda.'), error banner with 'Tentar novamente', offline notice; screenshots at both viewports.
- [ ] Timing: a Playwright script with the waiter signed in and the shift open goes from the list to the comanda screen in under 10 seconds of scripted interaction (keypad: at most 3 taps for a two-digit table).
