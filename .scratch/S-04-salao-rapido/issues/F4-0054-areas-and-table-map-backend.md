# F4-0054: Areas and the table map derived from comandas

**What to build:** Tables belong to areas; the map is computed from open comandas (free, occupied, waiting for the bill) with time since opening; a comanda can move to another table.
**Blocked by:** None
**Touches:** backend/src/Comandas/**, backend/migrations/**, backend/src/Api/**, backend/tests/**
**Nature:** objective
**tdd-scope:** required
**Seams:** Domain (pure); Real database (Testcontainers, app credential); Contract; Two tenants over HTTP (`DoisTenantsEndpointsTests` extended)
**Status:** ready-for-agent

- [ ] Migration for areas and the table-area link; unit-scoped; RLS; `SchemaDrift` test; table status stays computed, never a column.
- [ ] Map route returns areas with tables, status, number of open comandas and the opening time of the first one; 'waiting for the bill' appears once ticket 24 lands (status enum ready now).
- [ ] Move a comanda to another table: `comanda:transferir-mesa`, optimistic `versao`, audited, event `comanda.transferida`; refused to a table of another unit.
- [ ] Manage areas/tables permissions from the catalog; audit; contract regenerated.
- [ ] The 'use tables' option off hides the map data (empty) without error.
