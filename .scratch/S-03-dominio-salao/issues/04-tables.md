# 04: Tables (mesas) and the use-tables option

**What to build:** A manager registers tables of a unit (number or name, optional capacity); the unit can turn the use of tables on or off. Tables are listed for the waiter. Status is not stored: it is derived later from open comandas.
**Blocked by:** 01
**Touches:** backend/src/Comandas/** (mesas), backend/src/Api/** (routes), backend/migrations/0017_*.sql, backend/tests/Flow4Tech.Tests/Mesas*Endpoints*.cs, permissions/audit/settings catalogs, openapi.json, schema.ts
**Nature:** objective
**tdd-scope:** required
**Seams:** HTTP with real PostgreSQL; Architecture gates
**Status:** done

- [ ] Migration adds `mesas` (tenant table, RLS, composite FKs): unit, number or name unique per unit, optional capacity, active; no status column.
- [ ] Setting `operacao.usar-mesas` (boolean, organization and unit, default on) declared and exported; with it off the list route returns no tables and creating one is refused.
- [ ] Create and list routes with `mesa:ver` and `mesa:gerenciar`; `mesa:criar` audit record; denied and cross-organization cases tested; contract and catalogs regenerated.
