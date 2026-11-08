# F4-0050: Options and add-ons on products

**What to build:** A product can have option groups (add-ons with price, removals without cost, required choices with min/max); a launched item carries the chosen options with name and price frozen.
**Blocked by:** F4-0048
**Touches:** backend/src/Cardapio/**, backend/src/Comandas/**, backend/migrations/**, backend/src/Api/**, backend/tests/**
**Nature:** objective
**tdd-scope:** required
**Seams:** Domain (pure); Real database (Testcontainers, app credential); Contract; Two tenants over HTTP (`DoisTenantsEndpointsTests` extended)
**Status:** ready-for-agent

- [ ] Migration for option groups, options, product-group links and `item_opcoes` (frozen name/price); tenant RLS; `SchemaDrift` test.
- [ ] Domain validation: min/max, required groups, option belongs to the group, inactive option refused; the item total includes option prices in cents.
- [ ] Launch route accepts chosen options (ids only, no prices from the client); the server recomputes; a round with an invalid choice is refused as a whole.
- [ ] Routes to manage option groups with `cardapio:produto-editar`-style permission from the catalog; audit; contract regenerated.
- [ ] Frequency data for options feeds 'most used add-ons' (used by ticket 08).
