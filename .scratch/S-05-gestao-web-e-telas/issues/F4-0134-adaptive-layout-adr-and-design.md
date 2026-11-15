# F4-0134: ADR of the screen breakpoints and the DESIGN.md section for several screens

**What to build:** block R1 of slice S-05 (see the spec).
**Blocked by:** F4-0132
**Touches:** docs/adr/019-*.md, docs/design/DESIGN.md
**Nature:** docs
**tdd-scope:** not-applicable
**Status:** ready-for-agent

- [ ] ADR-019 fixes the three breakpoints (phone, tablet, computer) starting from the media queries of the prototype `index.html` (`#m=mobile` below 500 px; `#m=web` compaction between 1000 and 1280 px) and the owner rule: commercial standard sizes, no reference device.
- [ ] DESIGN.md gains the section 'Several screens and web management': breakpoints, navigation per breakpoint (bottom tabs, rail, sidebar as in the prototype), sheets that become side panels or dialogs, density, keyboard shortcuts (N, '3 chope'+Enter, Ctrl+K, Esc, `[`), focus rules.
- [ ] Every rule cites the prototype URL it comes from; nothing is invented that the prototype does not show, and gaps are listed as owner questions.
