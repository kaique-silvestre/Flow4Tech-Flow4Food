# 002. PostgreSQL with row-level security (RLS) for tenant isolation

- Status: proposed (draft from spike 06; accept after the data-access choice, ticket 07, confirms the driver pattern)
- Date: 2026-11-01

## Context
One shared database serves many organizations (tenants). Isolation is the top security priority: the database itself must refuse rows of another organization even if the application gets a query wrong (03-seguranca S5). Spike 06 proved the mechanism end to end against real PostgreSQL 17 (Testcontainers) with the real application credential.

## Decision
PostgreSQL with RLS, one shared schema.
- Every business table has `organizacao_id uuid NOT NULL` (the root table `organizacoes` uses `id`), `ENABLE` and `FORCE ROW LEVEL SECURITY`, and one policy `USING` and `WITH CHECK` on `(SELECT app_organizacao_id())`.
- Two credentials. Owner/migration role: owns the schema, runs `backend/migrations`. Application role `flow4tech_app`: not owner, not superuser, `NOBYPASSRLS`, no DDL, no access to `schema_migrations`; gets DML through default privileges.
- Context is set per transaction with `set_config('app.organizacao_id', <id>, true)` (the parameterizable form of `SET LOCAL`), only through `TenantDatabase.BeginAsync`. `app_organizacao_id()` reads it with `NULLIF(..., '')::uuid`.
- An architecture test inspects the catalog of the migrated database and fails on any table without the tenant column, without enabled+forced RLS, or without a policy on `app_organizacao_id()`.
- Migrations: plain versioned SQL in `backend/migrations`, applied by `MigrationRunner` (advisory lock, one transaction per file, `schema_migrations` bookkeeping) with the owner credential.

Minimum version: PostgreSQL 16 (spike ran on 17). Relied on: `CREATE ROLE` by a CREATEROLE role granting admin on the new role (16+) and no CREATE on `public` for PUBLIC (15+, also revoked explicitly in `0001`).

## Findings from the spike
1. After a transaction that used `set_config(..., true)` ends, the setting is the empty string, not NULL. The function must use `NULLIF(..., '')`, otherwise `''::uuid` raises an error for the next user of the connection.
2. Without context the predicate is NULL: reads return zero rows, writes fail with SQLSTATE 42501 (WITH CHECK). No silent write succeeds.
3. `SET row_security = off` is not a bypass for the app role: PostgreSQL raises 42501 instead of returning rows.
4. `FORCE ROW LEVEL SECURITY` also binds the owner. Consequence: seeds and data-fixing migrations must run inside a tenant context (or use a dedicated BYPASSRLS maintenance role, decided when the first backfill needs it). Test seeding uses `TenantDatabase` with the owner credential.
5. Foreign-key checks bypass RLS. A plain FK could point to another tenant's row, so child tables use composite FKs `(organizacao_id, parent_id) -> parent (organizacao_id, id)`; proven by test.
6. Reusing one physical connection (pool size 1, same `pg_backend_pid`) for tenant A, then B, then no tenant shows no leak, including with the Npgsql reset-on-close disabled.
7. Limit of the model: RLS trusts whoever sets `app.organizacao_id`. Any SQL running as the app role can set it (SQL injection with stacked statements, or code bypassing `TenantDatabase`). Mitigations: parameterized SQL only, `TenantDatabase` as the sole connection source (enforce by architecture test in a later ticket), organization id taken from the authenticated token only. A stronger option (context via a signed token verified in the database) is deferred.
8. Background jobs and event handlers must open `TenantDatabase.BeginAsync` with the organization of the job/event (outbox, ticket 08); there is no ambient context.

## Open items
- `src/Infrastructure/Data` is excluded from the module rules (`ModuleGraph.Parse`) but nothing yet enforces that modules reach tenant data only through `TenantDatabase` or that the Data project stays free of business rules. Decide in ticket 07 (data-access choice): either an architecture rule on who may reference/use Npgsql directly, or move the context API behind a Kernel interface.
- Ticket 08 (outbox/jobs): define how a job/handler restores the organization context (`BeginAsync` per job) and add an isolation test for it.

## Consequences
Easier: isolation holds even if a query forgets its `WHERE organizacao_id`; the rule is checkable in CI. Harder: every new table needs the boilerplate (policy, FORCE, composite FKs); owner-side data fixes need a context; the per-transaction `set_config` costs one extra statement per unit of work (not measured yet; measure in the performance spike).

## Alternatives considered
- Schema or database per tenant: strong isolation but heavy operations and migrations at the target scale; kept as a future option for very large customers (decision 010).
- Application-only filtering (ORM global filters): one forgotten filter leaks data; rejected as the sole defense.
- Session-level `SET` per connection: leaks through pooling; rejected.
