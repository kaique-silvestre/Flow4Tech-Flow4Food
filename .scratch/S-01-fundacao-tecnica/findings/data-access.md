# Findings: data access (Dapper vs EF Core) and migration runner (ticket 07)

Decision recorded in `docs/adr/002-postgresql-com-rls.md`: **EF Core with SQL kept visible and testable** (owner decision; the spike's own recommendation on speed and SQL clarity was Dapper, numbers below are the evidence for EF's risks), **keep own SQL MigrationRunner (now with checksums), no EF migrations**.
Spike code: `backend/spikes/data-access` (disposable, outside `Flow4Tech.sln`, see its README). Versions: Dapper 2.1.89, EF Core 10.0.14 + Npgsql EF provider 10.0.3, Npgsql 10.0.3, DbUp 7.0.1, Flyway OSS 11.20.3 (docker `flyway/flyway:11`), PostgreSQL 17.11 (`postgres:17-alpine`), .NET SDK 10.0.202.

## Use case and tests
Same use case behind `IItemStore` (add, list, add-then-fail) implemented three ways (raw Npgsql baseline, Dapper, EF Core); all run on the transaction from `TenantDatabase.BeginAsync` with the app credential and forced RLS. `UseCaseTests` (x3 impls = 12 tests): own-tenant insert/query; cross-tenant insert refused with 42501; no leak through a pool of size 1 (same physical connection); rollback discards write and context. `MisuseTests` (4): Dapper without the `transaction:` argument still runs inside it on Npgsql; EF without `UseTransactionAsync` still enlists in the open connection transaction (measured: no exception, rollback discards the write); bypassing `TenantDatabase` with a raw data source sees zero rows (Dapper and EF). The ticket 06 isolation tests are unchanged and use `TenantDatabase` (the data path both options sit on): 16 pass.

## Comparison
| Criterion | Dapper | EF Core |
|---|---|---|
| SQL clarity | SQL is the code; reviewable next to policies | LINQ generates SQL; to see it you need logging; composite FKs and `organizacao_id` mapped by hand per table |
| Typing | Records; names in SQL strings, checked only at runtime (needs an integration test per query) | Compile-time LINQ and mapped model; model must be kept in sync with SQL migrations by hand (we do not use EF migrations) |
| Context per transaction | Uses `tx.Connection` + `tx.Transaction` directly; nothing else to configure | Needs `DbContext(connection)` + `UseTransactionAsync`; works, but one more thing to remember; pooled `DbContext` factories do not fit (connection is per unit of work) |
| Performance | See below: about raw speed; 31 KiB / list | 1.2 to 2x slower on writes in this setup, 3x allocation, about 1 s first-call model build |
| Testability | Same integration tests as raw; no extra seam | Same, plus the model/mapping must be covered |

## Performance method and numbers
Hardware: Intel Core i7-9750H (12 threads), 16 GB, macOS (Darwin 25.6), Docker Desktop 29.7.2; PostgreSQL 17 in the container, reached through the Docker Desktop port-forward (adds latency to every round trip, so absolute ms are higher than a server on a LAN; relative ordering is what matters). Release build, `Maximum Pool Size=20`, app credential, RLS on, one tenant with 100 rows (list) and one inserted-into tenant. 200 warm-up + 1500 timed iterations per scenario, sequential; `Stopwatch` per op; allocation from `GC.GetTotalAllocatedBytes(true)` over the run; one implementation per process, 3 rounds interleaved (raw, dapper, ef). Not a statistical benchmark: noise between rounds is about +-10% (ranges below). Opt-in: `SPIKE_BENCH=1 SPIKE_IMPL=... dotnet test ... --filter Benchmark`.

| Scenario (median, p95) | raw | Dapper | EF Core |
|---|---|---|---|
| insert 1 row: begin+set_config+insert+commit | 1.75 to 2.10 ms (p95 2.9 to 3.2) | 1.89 to 2.26 ms (p95 2.9 to 3.9) | 3.62 to 3.96 ms (p95 5.5 to 6.4) |
| list 100 rows incl. begin+set_config+commit | 1.81 to 1.97 ms (p95 2.1 to 2.5) | 1.75 to 2.11 ms (p95 1.9 to 2.8) | 2.25 to 2.74 ms (p95 3.1 to 3.8) |
| alloc per insert / per list | 6.5 / 18.7 KiB | 6.8 / 30.8 KiB | 68.4 / 100.5 KiB |
| first list / first insert in a fresh process | 9.4 to 11.0 / 1.9 to 2.2 ms | 41.5 to 43.9 / 19.5 to 20.5 ms | 805 to 1087 / 148 to 188 ms |
| 16 parallel workers, list 100 rows | 2471 to 2559 ops/s | 2122 to 2346 ops/s | 1715 to 2228 ops/s |

Cost of the per-transaction context (raw path): `BEGIN; SELECT 1; COMMIT` 0.97 ms median vs `BeginAsync` (adds `set_config`) + `SELECT 1` + commit 1.45 to 1.50 ms, about +0.5 ms = one extra round trip (1.9 vs 4.1 KiB). This closes the "not measured" item in ADR-002; it grows with latency to the DB, so measure again on the real deployment network.
Also fixed during measurement: a first draft listed the growing insert tenant (about 1800 rows) and was discarded; numbers above are from the corrected run.

## Migration runner comparison (real `0001`, owner credential, fresh database; `MigrationRunnerComparisonTests`)
| | own MigrationRunner | DbUp 7.0.1 | Flyway OSS 11.20.3 (Docker) | EF migrations |
|---|---|---|---|---|
| applies 0001 (roles, RLS, FORCE, policies) | yes, 19 to 22 ms | yes, 57 to 62 ms | yes, about 2.7 s incl. container start (needed `baselineOnMigrate` only because the fixture left a stub table) | not run |
| journal readable by app role | no (42501) | no | no | n/a |
| edited applied file | silently ignored before; **now refused** (checksum added in this ticket) | silently ignored | refused (checksum) | n/a |
| two simultaneous runners | serialized (advisory lock) | one of two failed | not tested (has its own locking) | n/a |
| cost | 0 dependencies, about 100 lines to own | 1 package; no lock, no checksum, so nothing gained | Java image 940 MB, file rename `V<n>__x.sql`, separate toolchain in CI | pulls EF Core only for tooling; with Dapper they would wrap `migrationBuilder.Sql` |
Decision: keep own runner, add checksum (implemented, 5 tests in `tests/Isolation/MigrationRunnerIntegrationTests.cs`). EF migrations were argued from the choice of Dapper, not executed.

## Architecture rule (ADR-002 open item closed)
`ModuleGraph.CheckTenantDataAccess` (tests/Architecture, 15 new test cases with violating fixtures + real tree): Npgsql only in `src/Infrastructure/Data` (package, `using`, or code mention); Dapper only in Data and module Infrastructure; a module may reference the Data project only from its Infrastructure layer (other layers hit the existing "only Kernel is allowed outside modules" rule); EF Core packages banned in `src`. The real tree check found one existing violation: `src/Api/Program.cs` uses Npgsql for the readiness `SELECT 1`. Kept as an explicit, commented exemption for `src/Api` (no tenant table) to remove when the probe moves into Data. Still open, by design: ticket 08 job/handler context restore.

## Fix 1: EF Core as the decision (real code path in src/Infrastructure/Data)
- `TenantDbContext` (abstract, Data project): only constructor takes `TenantTransaction` (internal ctor, only from `TenantDatabase.BeginAsync`); joins the transaction (`Database.UseTransaction`) and is configured by a sealed `OnConfiguring` (`UseNpgsql(tx.Connection)` + `TenantCommandGuard`). The guard (DbCommandInterceptor) throws `TenantContextRequiredException` for any command whose transaction is not the current, un-ended tenant transaction. Note: Npgsql reuses the `NpgsqlTransaction` instance of a pooled connector, so the guard maps instance -> current `TenantTransaction` and `Ended` is set on commit/dispose.
- Measured behaviour: after `CommitAsync`, EF/Npgsql already throw `InvalidOperationException: Transaction is already completed`; the guard is the second line and is proven by detaching the transaction (`Database.UseTransactionAsync(null)`) which would otherwise run with no context (zero rows) silently.
- `SchemaDrift.FindAsync(TenantDbContext)` compares the EF model with the migrated schema (`pg_attribute`): proven on the sample table (no drift) and on a deliberately wrong context (missing column, wrong type, nullability).
- Generated SQL asserted with `ToQueryString()`: list = `SELECT a.id, a.nome, a.organizacao_id, a.unidade_id, a.valor_centavos FROM amostra_itens AS a ORDER BY a.nome`, by-id with parameter `@id`, `FromSql` parameterized `@p0`; none filters on `organizacao_id` in the application.
- Raw SQL: interpolated `FromSql` and `ExecuteSqlAsync` run in the tenant transaction and respect RLS (B's row invisible; update touches only A's row).
- Architecture rules (38 tests in tests/Architecture): Npgsql only in Data (+ Api probe exemption); EF Core only in Data and module Infrastructure; `: DbContext`, `AddDbContext*`, `UseNpgsql`, `DbContextOptions` only in Data; `FromSqlRaw`/`ExecuteSqlRaw`/`SqlQueryRaw` banned; EF Design/Tools and Dapper banned in `src`; scanner tested on code vs comments.
- Not measured: the effect of the warm-up mitigation (stated in ADR as mitigation, untested); `SqlQuery<T>` is allowed by the ADR but has no test.
- Docs referencing the old rule that this ticket does NOT own (report only): `.scratch/S-01-fundacao-tecnica/spec.md` line 36 ("SQL explícito sem ORM que esconda SQL") and lines 19/70/100 (Dapper/EF choice wording); `.scratch/S-01-fundacao-tecnica/packets/F4-0001.md` line 26; `.scratch/S-00-novo-sistema/04-arquitetura.md` lines 83 ("Acesso a dados SQL-first ... sem ORM que esconda SQL") and 130 ("Dapper ... e/ou EF Core"), 280; `.scratch/S-00-novo-sistema/06-roadmap.md` line 47; ticket `issues/F4-0007-data-access-choice.md` title.

## Rules added to CLAUDE.md (section "Data and tenant isolation", plus the Conventions line "Data:")
1. EF Core with SQL kept visible and testable; every tenant query inside `TenantDatabase.BeginAsync`; contexts derive from `TenantDbContext`, built from the `TenantTransaction`; no AddDbContext/UseNpgsql/pooled/long-lived contexts.
2. Schema is SQL: no EF migrations/EnsureCreated; per-context `SchemaDrift` integration test.
3. SQL visible: `AsNoTracking`, `ToQueryString()` tests for critical queries, no application-side tenant filter; raw SQL only via interpolated `FromSql`/`ExecuteSqlAsync`/`SqlQuery`; warm the model at host start.
4. Architecture rule summary; migrations runner stores SHA-256 and throws on edits; `backend/spikes/**` disposable.
