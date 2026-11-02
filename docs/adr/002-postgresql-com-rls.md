# 002. PostgreSQL with row-level security (RLS) for tenant isolation

- Status: accepted (spike 06 proved the mechanism; ticket 07 chose the data-access style and confirmed the migration runner; ticket 08 closed the background-job context item)
- Date: 2026-11-01

## Context
One shared database serves many organizations (tenants). Isolation is the top security priority: the database itself must refuse rows of another organization even if the application gets a query wrong (03-seguranca S5). Spike 06 proved the mechanism end to end against real PostgreSQL 17 (Testcontainers) with the real application credential.

## Decision
PostgreSQL with RLS, one shared schema.
- Every business table has `organizacao_id uuid NOT NULL` (the root table `organizacoes` uses `id`), `ENABLE` and `FORCE ROW LEVEL SECURITY`, and one policy `USING` and `WITH CHECK` on `(SELECT app_organizacao_id())`.
- Two credentials. Owner/migration role: owns the schema, runs `backend/migrations`. Application role `flow4tech_app`: not owner, not superuser, `NOBYPASSRLS`, no DDL, no access to `schema_migrations`; gets DML through default privileges.
- Context is set per transaction with `set_config('app.organizacao_id', <id>, true)` (the parameterizable form of `SET LOCAL`), only through `TenantDatabase.BeginAsync`. `app_organizacao_id()` reads it with `NULLIF(..., '')::uuid`.
- An architecture test inspects the catalog of the migrated database and fails on any table without the tenant column, without enabled+forced RLS, or without a policy on `app_organizacao_id()`.
- Migrations: plain versioned SQL in `backend/migrations`, applied by `MigrationRunner` (advisory lock, one transaction per file, `schema_migrations` bookkeeping with a SHA-256 checksum per file; editing an applied file is refused) with the owner credential.
- Data access: **EF Core with SQL kept visible and testable** (owner decision, see "Data-access choice"). Every tenant query runs inside `TenantDatabase.BeginAsync`, and a `DbContext` is never used outside it: tenant contexts derive from `TenantDbContext` (in `src/Infrastructure/Data`), whose only constructor takes the `TenantTransaction`, so a context cannot exist without an open tenant transaction; it joins that transaction on its connection, and an interceptor refuses any command that is not inside a live tenant transaction on the same connection (`TenantContextRequiredException`).
- Schema and RLS are SQL, applied by `MigrationRunner` (not EF migrations): policies, `FORCE`, roles, composite FKs and the one-policy-per-table rule live in `backend/migrations/*.sql`, are applied with the owner credential, and are checked against the real catalog by `RlsArchitectureIntegrationTests`. EF only maps tables that already exist; `SchemaDrift.FindAsync(context)` (Data project) compares the EF model with the migrated database (table, column, type, nullability) and every module context must have an integration test calling it.
- Raw SQL for hot or complex queries is allowed only as interpolated `DbSet.FromSql($"...")`, `Database.ExecuteSqlAsync($"...")` or `Database.SqlQuery<T>($"...")` on the tenant context (parameterized, same transaction, same RLS). `FromSqlRaw`, `ExecuteSqlRaw` and `SqlQueryRaw` are banned.
- Enforcement (`tests/Architecture`, `ModuleGraph.CheckTenantDataAccess`): `Npgsql` only in `src/Infrastructure/Data` (plus the documented readiness-probe exemption in `src/Api`); EF Core packages and namespaces only in Data and module Infrastructure layers; `: DbContext` (also with namespace prefix, after a comma in the base list, or through a `using Alias = ...DbContext`), `AddDbContext*`, `UseNpgsql`, `DbContextOptions` and the literal `app.organizacao_id` only in Data; EF Design/Tools and Dapper banned in `src`; only a module Infrastructure layer may reference the Data project.

Minimum version: PostgreSQL 16 (spike ran on 17). Relied on: `CREATE ROLE` by a CREATEROLE role granting admin on the new role (16+) and no CREATE on `public` for PUBLIC (15+, also revoked explicitly in `0001`).

## Findings from the spike
1. After a transaction that used `set_config(..., true)` ends, the setting is the empty string, not NULL. The function must use `NULLIF(..., '')`, otherwise `''::uuid` raises an error for the next user of the connection.
2. Without context the predicate is NULL: reads return zero rows, writes fail with SQLSTATE 42501 (WITH CHECK). No silent write succeeds.
3. `SET row_security = off` is not a bypass for the app role: PostgreSQL raises 42501 instead of returning rows.
4. `FORCE ROW LEVEL SECURITY` also binds the owner. Consequence: seeds and data-fixing migrations must run inside a tenant context (or use a dedicated BYPASSRLS maintenance role, decided when the first backfill needs it). Test seeding uses `TenantDatabase` with the owner credential.
5. Foreign-key checks bypass RLS. A plain FK could point to another tenant's row, so child tables use composite FKs `(organizacao_id, parent_id) -> parent (organizacao_id, id)`; proven by test.
6. Reusing one physical connection (pool size 1, same `pg_backend_pid`) for tenant A, then B, then no tenant shows no leak, including with the Npgsql reset-on-close disabled.
7. Limit of the model: RLS trusts whoever sets `app.organizacao_id`. Any SQL running as the app role can set it (SQL injection with stacked statements, or code bypassing `TenantDatabase`). Mitigations: parameterized SQL only, `TenantDatabase` as the sole connection source (enforced by architecture test since ticket 07), organization id taken from the authenticated token only. A stronger option (context via a signed token verified in the database) is deferred.
8. Background jobs and event handlers must open `TenantDatabase.BeginAsync` with the organization of the job/event; there is no ambient context. Implemented and tested under RLS in ticket 08 (`OutboxDispatcher`, `Job.Organization`; see ADR-003).

## Data-access choice (ticket 07)
Spike `backend/spikes/data-access` (disposable, kept as evidence) implemented the same use case (insert and list `amostra_itens` under RLS, per-transaction context) with raw Npgsql, Dapper and EF Core 10 on top of `TenantDatabase`, against real PostgreSQL 17. Full numbers and method: `.scratch/fundacao-tecnica/findings/data-access.md`. Measured on an Intel i7-9750H, 12 threads, Docker Desktop PostgreSQL 17 (loopback port-forward), 1500 iterations after 200 warm-up, 3 runs each:

| | Dapper | EF Core | raw Npgsql (baseline) |
|---|---|---|---|
| insert 1 row (begin + set_config + insert + commit), median | 1.9 to 2.3 ms | 3.6 to 4.0 ms | 1.8 to 2.1 ms |
| list 100 rows, median | 1.8 to 2.1 ms | 2.2 to 2.7 ms | 1.8 to 2.0 ms |
| allocation per list / insert | 31 KiB / 6.8 KiB | 100 KiB / 68 KiB | 19 KiB / 6.5 KiB |
| first call in a fresh process | 42 / 20 ms | 810 to 1090 / 150 to 190 ms | 10 / 2 ms |
| 16 parallel workers, list | 2100 to 2350 ops/s | 1700 to 2230 ops/s | 2470 to 2560 ops/s |

Both options passed the same isolation assertions (cross-tenant insert refused with 42501, no leak through a size-1 pool, rollback discards write and context, bypassing `TenantDatabase` sees zero rows).

**Decision (owner): EF Core**, with SQL kept visible and testable. This replaces the earlier rule "explicit SQL, no ORM hiding SQL". The spike recommendation was Dapper on speed and SQL clarity; the owner chose EF Core for typed models, LINQ and change tracking across the many CRUD-shaped modules of Phase 1, accepting the measured costs below. The numbers stay here as the evidence for the risks.

| Risk of the decision | Measured / known | Mitigation |
|---|---|---|
| Startup cost | first query about 0.8 to 1.1 s (model build), first insert 150 to 190 ms | Warm the model at host start (touch `context.Model` and run one query per context type before readiness reports ready); compiled models (`dotnet ef dbcontext optimize` needs the Design tool, which is banned in `src`; revisit only if warm-up is not enough) |
| Allocation and write latency | insert 1.7x median, 10x allocation; list 1.2x, 3x allocation | `AsNoTracking()` for every read; `FromSql`/`ExecuteSql` or `ExecuteUpdate`/`ExecuteDelete` for hot paths; avoid loading entities to update them |
| SQL hidden by LINQ | queries are generated | Critical queries have tests asserting the generated SQL text with `ToQueryString()` (see `TenantDbContextIntegrationTests`), including "no application-side tenant filter" (RLS is the filter); SQL logging at Information in non-production |
| `DbContext` misuse (used outside the tenant transaction, long-lived or shared, pooled factories, `AddDbContext`) | EF would otherwise run on any connection | `TenantDbContext` is constructible only from a `TenantTransaction`; interceptor refuses commands outside a live tenant transaction; architecture tests ban `: DbContext`, `AddDbContext*`, `UseNpgsql`, `DbContextOptions` outside Data; context lifetime is exactly one unit of work |
| Model/schema drift (EF does not own the schema) | none measured; possible by design | `SchemaDrift.FindAsync` integration test per module context; the migrated database is the source of truth |
| Unparameterized raw SQL | `FromSqlRaw`/`ExecuteSqlRaw` accept concatenation | banned by architecture test; interpolated variants parameterize |

### Migration runner (re-evaluated with the same spike; EF migrations vs our SQL runner)
Real migration `0001` applied by each candidate with the owner credential: own `MigrationRunner` (about 20 ms), DbUp 7 (about 60 ms), Flyway OSS 11.20 in Docker (about 2.7 s including container start, 940 MB image, Java toolchain, files renamed to `V<n>__name.sql`). All three produced the same schema and none exposes its journal table to the app role (the journal is created before the default privileges). Differences: editing an applied file is refused by Flyway (checksum) and was silently ignored by our runner and DbUp; two simultaneous runners were serialized by our advisory lock, while one of two concurrent DbUp runs failed.
**Decision: keep our SQL runner, even with EF Core as the data-access style; no EF migrations.** Reasons: the security-critical objects (roles, `FORCE ROW LEVEL SECURITY`, the single `FOR ALL TO PUBLIC` policy per table, composite tenant FKs, default privileges, `set_config` function) are SQL that EF migrations cannot express except as `migrationBuilder.Sql` blobs, which would hide them inside generated C# and a model snapshot that can diverge from them; the architecture test inspects the real catalog regardless of tool; the runner is about 100 lines and now refuses edits to applied files via SHA-256 checksums (tests in `tests/Isolation/MigrationRunnerIntegrationTests.cs`). EF Design/Tools are banned in `src` to keep it that way. Guard against model drift: `SchemaDrift` test per context (above). Revisit Flyway only if migrations need repeatable scripts, undo, or are run by ops outside the application image. DbUp offers nothing over the current runner (no lock, no checksum).

## Open items
- Closed in ticket 08 (ADR-003): jobs carry the `OrganizationContext` and the handler/dispatcher opens `BeginAsync` with it; tests in `tests/Outbox` prove a handler that restores it sees only its organization and one that does not sees zero rows with writes refused (42501). New open item from it: how a dispatcher discovers which organizations have pending events (RLS hides them without a context), see ADR-003 open items; likely the same BYPASSRLS maintenance role as backfills.
- The architecture rule is static, but the runtime pieces close most of the gap: a `TenantDbContext` cannot be built without a `TenantTransaction`, and commands outside a live one throw. A module can still hold a context past its unit of work only to have every command refused. The `src/Api` Npgsql exemption (readiness `SELECT 1`) should disappear when the probe moves into the Data project.
- Known limit: `TenantTransaction.Connection` is public (callers need it for raw commands), so code holding a transaction could run its own `set_config('app.organizacao_id', ...)` and switch tenant inside the unit of work. Mitigations: an architecture test bans the literal `app.organizacao_id` in `src` outside the Data project (migrations are SQL, not scanned), parameterized SQL only, review. A stronger design (context bound to a signed token verified in the database, or not exposing the raw connection) is deferred with finding 7.
- Test project `tests/Isolation` seeds through `TenantDatabase` with the owner credential; a BYPASSRLS maintenance role for backfills is still to be decided when the first backfill needs it (the outbox organization discovery in ADR-003 may need it first).

## Consequences
Easier: isolation holds even if a query forgets its `WHERE organizacao_id`; the rule is checkable in CI. Harder: every new table needs the boilerplate (policy, FORCE, composite FKs); owner-side data fixes need a context; the per-transaction `set_config` costs one extra statement per unit of work (measured in ticket 07: about 0.5 ms median with Docker Desktop loopback latency, 1.5 ms against 1.0 ms for `BEGIN; SELECT 1; COMMIT`; it is one extra round trip, so it scales with network latency to the database, not with data).

## Alternatives considered
- Dapper over `TenantDatabase` (the spike's performance and clarity winner): not chosen by the owner; evidence kept above. Its rules were removed from the architecture test (Dapper is banned in `src`).
- Schema or database per tenant: strong isolation but heavy operations and migrations at the target scale; kept as a future option for very large customers (decision 010).
- Application-only filtering (ORM global filters): one forgotten filter leaks data; rejected as the sole defense.
- Session-level `SET` per connection: leaks through pooling; rejected.
