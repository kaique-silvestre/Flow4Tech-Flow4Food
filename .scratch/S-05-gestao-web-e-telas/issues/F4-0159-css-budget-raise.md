# F4-0159: CSS budget raised to 9.5 kB (owner approval)

**What to build:** governança: o dono aprovou subir o teto de CSS gzip do app de 9.0 para 9.5 kB (2026-11-15), porque as telas do bloco G (S-05) precisam de CSS e o teto estava a 20 bytes do limite (F4-0136 e F4-0137 já tinham pedido ajuste).
**Blocked by:** F4-0158
**Touches:** frontend/app/bundle-budget.json, docs/agents/ci-gates.md
**Nature:** objective
**tdd-scope:** not-applicable
**Status:** done

- [x] Teto em frontend/app/bundle-budget.json: cssTotalGzipKb 9.0 -> 9.5.
- [x] Razão e data registradas em docs/agents/ci-gates.md.
