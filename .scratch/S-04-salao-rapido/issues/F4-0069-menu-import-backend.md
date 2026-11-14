# F4-0069: Menu import from a spreadsheet

**What to build:** The owner uploads a spreadsheet of categories, products and options; the server validates every row, previews, and applies atomically and idempotently.
**Blocked by:** F4-0048, F4-0050
**Touches:** backend/src/Cardapio/**, backend/migrations/**, backend/src/Api/**, backend/tests/**
**Nature:** objective
**tdd-scope:** required
**Seams:** Domain (pure); Real database (Testcontainers, app credential); Contract; Two tenants over HTTP (`DoisTenantsEndpointsTests` extended)
**Status:** done

- [ ] Template description and a parser for CSV and XLSX behind one interface (format confirmed with the owner; question Q7 in the spec); size and row limits; no formulas executed, no macros.
- [ ] Validation returns per-row errors (line, column, message) and a preview of creates/updates; nothing is written in the preview.
- [ ] Apply runs in one unit of work, idempotent by import id; either all rows apply or none; permission `cardapio:importar` [A]; audit with counts only; event `cardapio.importado`.
- [ ] Matching by short code so a second import updates instead of duplicating; prices in cents, never float.
- [ ] Real file samples from the restaurant added as fixtures once available.
