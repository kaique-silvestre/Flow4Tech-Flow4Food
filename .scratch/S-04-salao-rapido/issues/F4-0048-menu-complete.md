# F4-0048: Complete menu: two-level categories, promotional price, price per unit, 'acabou'

**What to build:** The menu supports what a real restaurant has: subcategories, a promotional price that overrides the normal price while valid, a unit-specific price, and a manual 'acabou' switch, all frozen into the item at launch as today.
**Blocked by:** None
**Touches:** backend/src/Cardapio/**, backend/migrations/**, backend/src/Api/**, backend/tests/**
**Nature:** objective
**tdd-scope:** required
**Seams:** Domain (pure); Real database (Testcontainers, app credential); Contract; Two tenants over HTTP (`DoisTenantsEndpointsTests` extended)
**Status:** done

- [ ] Migration: category parent (max two levels), promotional price with validity window, unit price override table, `indisponivel` flag; tenant tables with exact RLS and composite FKs; `SchemaDrift` test.
- [ ] Pure domain rule for the effective price (unit override > promotional while valid > base) with tests including window edges (UTC, `IClock`).
- [ ] An 'acabou' or inactive product cannot be launched (existing refusal extended); the item keeps the effective price and name frozen at launch.
- [ ] Routes to create/change categories and products with the new fields, permissions `cardapio:preco-alterar` [A][L] (percent limit through the engine) and the existing ones; audit entries; contract regenerated.
- [ ] Two-tenant test: a unit override in one unit does not leak to another unit or organization.
