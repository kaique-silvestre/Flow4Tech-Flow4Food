# F4-0136: Salão on the tablet: list and detail side by side

**What to build:** block R3 of slice S-05 (see the spec).
**Blocked by:** F4-0135
**Touches:** frontend/app/src/features/**
**Nature:** ui
**tdd-scope:** required
**Status:** done

- [ ] On the tablet the comanda list and the open comanda show side by side; the mesas map, caixa, cardapio and pessoas screens use the width without losing any function of the phone.
- [ ] Sheets become side panels or dialogs with the same logic and routes.
- [ ] Playwright at 820x1180, light and dark; axe strict; captures reviewed against the prototype.

**Nota R3:** mesas, caixa, cardápio e pessoas herdam a largura do tablet pelo shell (trilho de 72 px mais a área de conteúdo em largura total, F4-0135); a função do celular já cabe nessa largura, então não mudaram.
