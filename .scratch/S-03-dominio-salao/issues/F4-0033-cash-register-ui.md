# F4-0033: Screen: cash register on the phone (open and close shift)

**What to build:** The operator sees whether they have an open shift; with none, a card to open the cash register with the float; with one, the expected cash and a Close action that asks the count and shows the result in words.
**Blocked by:** F4-0026, F4-0032
**Touches:** frontend/app/src/features/caixa/**, frontend/design-system/** (as needed), e2e specs
**Nature:** ui
**tdd-scope:** required
**Seams:** UI in the browser
**Status:** done

- [x] `/salao/caixa` shows 'Abrir caixa' with a float field when there is no open shift, and the open shift (opened at, float, expected cash) otherwise; the float is explicitly not a sale.
- [x] Closing asks the cash count (zero allowed) and shows 'Confere', 'Falta R$ x' (alert marker + word) or 'Sobra R$ x' (warning marker + word); amounts are BRL in cents formatted with tabular figures.
- [x] A second open in the same unit is shown as an error the user can act on; no permission (`caixa:abrir`) hides the action instead of disabling it.
- [x] States: loading skeleton, empty ('Caixa fechado. Abra o caixa para receber.'), error with retry; screenshots at 390x844 and 360x740.
- [x] Playwright flow against the real API: open with float, see the shift, close with a count, see the difference in words.
