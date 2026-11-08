# F4-0025: Minimal menu: products and categories

**What to build:** A manager can create and change products (name, short code, price in cents, category, active) and simple categories; a waiter can search by name or code and see the frequent items. Price changes never reach comandas already launched. Everything is tenant-isolated, permissioned, audited and emits events.
**Blocked by:** F4-0024
**Touches:** backend/src/Cardapio/**, backend/src/Api/** (routes), backend/migrations/0015_*.sql, backend/tests/Flow4Tech.Tests/Cardapio*Endpoints*.cs, backend/permissions.catalog.txt, backend/audit.catalog.txt, backend/src/Api/openapi.json, frontend/packages/api-client/src/schema.ts
**Nature:** objective
**tdd-scope:** required
**Seams:** HTTP with real PostgreSQL; Pure domain; Architecture gates
**Status:** done

- [ ] Migration adds `categorias` and `produtos` as tenant tables (organizacao_id, RLS enabled+forced, exact policy, composite FKs); product has unique short code per organization, price as `bigint` cents + currency `BRL`, active flag, `versao`; `RlsArchitectureIntegrationTests` passes with no new exemption and the EF context has a `SchemaDrift` test.
- [ ] Routes with `.WithName`, typed responses, `.RequireSession()` and `.RequirePermission(...)`: list, search (name or code), frequent items, create/alter product and category; no route takes organization or unit from the client.
- [ ] Search by name or short code returns the active products matching; inactive products are not offered for launch; frequent items are the unit's most launched products (empty list when there is no history).
- [ ] Create and alter write `produto:criar` / `produto:alterar` audit records in the same unit of work and emit a stable domain event; secrets rule respected.
- [ ] Allowed and denied (403 without reason) and cross-organization cases are tested; the permission matrix test lists the new permissions; catalogs regenerated and committed.
- [ ] OpenAPI document and TypeScript client regenerated and committed; `oasdiff` reports no breaking change.
