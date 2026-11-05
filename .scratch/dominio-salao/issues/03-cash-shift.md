# 03: Cash register: open and close a shift

**What to build:** An operator opens a shift with a float (change fund), at most one open shift per operator and unit; closes it by counting the cash, sees expected, difference in words (Confere / Falta R$ x / Sobra R$ x) and the shift ends. The float is not a sale. Includes the unit option to require an open shift to receive cash.
**Blocked by:** 01
**Touches:** backend/src/Caixa/**, backend/src/Api/** (routes), backend/migrations/0016_*.sql, backend/tests/Flow4Tech.Tests/Caixa*Endpoints*.cs, permissions/audit/settings catalogs, backend/src/Api/openapi.json, frontend/packages/api-client/src/schema.ts
**Nature:** objective
**tdd-scope:** required
**Seams:** HTTP with real PostgreSQL; Pure domain; Architecture gates
**Status:** done

- [ ] Migration adds `turnos_caixa` (tenant table, RLS): unit, operator, opened_at, float cents, closed_at, counted cents, expected cents, difference cents; a partial unique index allows at most one open shift per operator and unit.
- [ ] Open accepts a float of zero; a second open by the same operator in the same unit is refused; the float never appears as sales.
- [ ] Close requires the cash count (zero allowed) and returns `expected = float + cash payments applied` (no supply, bleed or refund in this slice), the difference and the word form 'Confere', 'Falta R$ x' or 'Sobra R$ x'; closing always concludes (no approval flow).
- [ ] Setting `caixa.exigir-caixa-aberto` (boolean, organization and unit, default on) is declared in a [SettingsCatalog] class and exported to `backend/settings.catalog.txt`.
- [ ] `caixa:abrir` and `caixa:fechar` audit records and `caixa.aberto` / `caixa.fechado` outbox events are written in the same unit of work; opening and closing are `sensitive: true` routes.
- [ ] Permissions `caixa:ver`, `caixa:abrir`, `caixa:fechar`, `caixa:ver-todos` declared; without `caixa:ver-todos` only the operator's own shift is visible; denied and cross-organization cases tested; contract and catalogs regenerated.
