# 06: RLS and tenant isolation spike

**What to build:** A minimal schema proves that PostgreSQL itself isolates organizations: a sample business table with RLS, two database credentials (owner/migration and application), organization context set per transaction, and the first two-tenant isolation test running with the real application credential.
**Blocked by:** 02
**Touches:** `backend/src/Kernel/**`, `backend/src/Infrastructure/Data/**`, `backend/migrations/**`, `backend/tests/Isolation/**`, `docs/adr/002-*.md`
**Nature:** objective
**tdd-scope:** required
**Seams:** Integration (real PostgreSQL, app credential), Architecture test
**Status:** done

- [ ] Tables `organizacoes`, `unidades` and one sample business table exist, each business table with `organizacao_id` and an RLS policy `organizacao_id = current context`
- [ ] Application connects with a role that does not own the tables and cannot bypass RLS; migrations use a different credential (both proven by tests)
- [ ] Organization context is set per transaction (`SET LOCAL` or equivalent), never per connection, and cannot leak across pooled connections (test reuses one connection for tenant A then tenant B)
- [ ] Two-tenant isolation test: tenant A cannot read, update or delete tenant B rows; a query with no context returns zero rows or an error
- [ ] Architecture test fails the CI when a business table has no `organizacao_id` or no RLS policy (proven with a violating migration)
- [ ] Draft of ADR-002 (PostgreSQL with RLS) records the findings
