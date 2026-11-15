# F4-0137: Salão on the computer with keyboard shortcuts

**What to build:** block R4 of slice S-05 (see the spec).
**Blocked by:** F4-0136
**Touches:** frontend/app/src/features/**
**Nature:** ui
**tdd-scope:** required
**Status:** done

- [ ] Same content as the tablet with the sidebar layout of the prototype `#m=web`; comandas as cards or table.
- [ ] Shortcuts work only on the comandas screen: N (new comanda), '3 chope'+Enter (add item), Ctrl+K (search), Esc (close), `[` (collapse the menu).
- [ ] Visible focus and correct tab order; Playwright at 1440x900, light and dark; axe strict.

## Deviations from the prototype and DESIGN.md (decided with the reviewer)
- Detail panel width 1000 to 1279 px: stays 420 px, not 380 px. The 380 px rule costs 23 bytes of gzip CSS (9239 B with it, ceiling 9216 B); the limit was not raised. Needs the owner's call (raise the ceiling or trim CSS elsewhere).
- "Recolher menu" stays in the foot of the sidebar; the prototype has an icon button in the top bar (l. 2570, `#sbtn`, aria-label "Recolher menu lateral"). Accepted: the position was built and approved in F4-0135, and moving it is a shell change outside this ticket.
- Tablet has no shortcuts until a reliable physical-keyboard detection exists (noted in ADR-019, "Respostas do dono").
- CSS made room by removing dead selectors (`.f4-root`, `.f4-icon-btn`) and merging the six reduced-motion blocks of the design system into one (two tests now match the rule inside the merged block).
