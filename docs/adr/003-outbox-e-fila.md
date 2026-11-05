# 003. Transactional outbox in PostgreSQL; queue/worker behind our own interface

- Status: accepted (spike ticket 08; staging latency still pending, completes in ticket 12)
- Date: 2026-11-02

## Context
Modules must publish domain events without ever losing one or announcing something that did not commit, and without a second system to keep consistent (04-arquitetura A5: queue in PostgreSQL first, interface abstracted so SQS or Kafka can replace it, worker inside the API process). Background work has no request, so there is no ambient organization: ADR-002 finding 8 left open how a job restores the organization context and how that is proved under RLS.

## Decision
1. **Own transactional outbox on PostgreSQL**, built on `TenantDatabase` and EF Core (`src/Infrastructure/Outbox`, events contract in `src/Kernel/Events`).
   - `OutboxWriter.AddAsync(tx, organization, event)` inserts into `outbox_eventos` on the CALLER's `TenantTransaction`: it never opens or commits a transaction, so state change and event commit or roll back together (tested: failure after the state change, after the outbox write, and a failing outbox insert each leave neither).
   - `OutboxDispatcher.DispatchAsync(organization)` delivers at least once. Per event it opens `TenantDatabase.BeginAsync(<the event's organization>)`, locks the oldest pending row with `FOR UPDATE SKIP LOCKED`, runs the consumers inside that transaction, marks `processado_em`, commits. Any failure (consumer, crash before the mark) rolls the transaction back: the event stays pending and is redelivered, never lost. Failures are counted (`tentativas`, `ultimo_erro`) and the event is parked (`falhou_em`) after `MaxAttempts` (default 5) so one poison event never blocks the rest. No global ordering (concurrent dispatchers and retries reorder); consumers must not assume it.
   - Idempotent consumers: `IdempotentConsumer` + inbox table `outbox_consumos (organizacao_id, evento_id, consumidor)`; the inbox mark is written on the same transaction as the consumer's effect, so a redelivery after a rollback re-applies the effect once and a duplicate after a commit is absorbed. Effects outside the database (email, HTTP) are not covered by that transaction and must be idempotent on the event id themselves.
   - Both tables are normal tenant tables (`0002_outbox.sql`: `organizacao_id`, ENABLE+FORCE RLS, the single isolation policy); `RlsArchitectureIntegrationTests` covers them with no edit.
2. **Context restore in background work (closes the ADR-002 open item).** Every job carries its `OrganizationContext` explicitly (`Job.Organization`); the handler restores it by opening `TenantDatabase.BeginAsync(job.Organization)` before touching data. The dispatcher does the same per event. Tested under RLS with the real app credential: a handler that restores the context sees only its organization's rows (A and B in the same worker, no inheritance between jobs); a handler that "forgets" (raw pooled connection) sees zero rows and its writes are refused with 42501.
3. **Queue/worker behind our own interface** `IBackgroundJobs` (`EnqueueAsync(Job)`, `StartAsync(JobHandler)`, `StopAsync`), delivery at least once. `src/Infrastructure/Outbox` ships `InProcessBackgroundJobs` (channel, not durable). The outbox table is what is durable; a lost wake-up job is recovered by a periodic sweep.
4. **Library chosen: Quartz.NET (4.x), RAM job store, behind `IBackgroundJobs`**, adapter written when the first ticket hosts the worker (the spike adapter in `backend/spikes/queue/Adapters.cs` is the starting point). Revisit trigger: durable delayed work that cannot be expressed as an outbox event, or moving consumers to a broker (SQS/Kafka); then adopt Wolverine behind the same interface.

## Comparison (spike `backend/spikes/queue`, disposable)
Each library adapted to `IBackgroundJobs` and run through identical scenarios against real PostgreSQL 17 (Testcontainers, app credential, RLS): handler restores org and sees only own rows (10 jobs for A and B); a handler that fails its first attempt is retried; 100 paced jobs for pickup latency after 10 warm-up; 300-job burst for throughput. Each job's handler does a real `BeginAsync` + insert + commit. Hardware: Intel i7-9750H, 12 threads, 16 GB, macOS, Docker Desktop PostgreSQL 17 over loopback. One run per library (indicative; ordering is clear, exact values are not).

| | Wolverine 6.60 | Hangfire 1.8.25 + Hangfire.PostgreSql 1.21 | Quartz 4.6.2 |
|---|---|---|---|
| passed the 3 scenarios | yes | yes | yes |
| pickup latency median / p95 (enqueue to handler done) | 4.1 / 5.1 ms | 14.5 / 16.2 ms (poll 100 ms) | 3.5 / 4.6 ms |
| burst throughput (300 jobs, sequential enqueue) | 919 jobs/s | 134 jobs/s | 1947 jobs/s |
| durable in PostgreSQL | yes (`wolverine` schema, created by the library with the owner credential; durable local queues) | yes (`hangfire` schema, created by the library) | spike used RAM store; the ADO.NET store needs the library's own DDL (not in the package), not tried |
| retry | policy (`RetryTimes`, cooldown, DLQ) | attribute (`AutomaticRetry`), default delays are minutes | none: the job must ask to be refired (`RefireImmediately`) |
| scheduling (cron, delays) | delays only; recurring is on us | recurring + delayed + dashboard | best of three (cron, calendars) |
| path to SQS/Kafka | native transports | none | none |
| cost/risk seen | needed `WolverineFx.RuntimeCompilation` (Roslyn at runtime) or a codegen step; convention-driven handler discovery; largest dependency graph (JasperFx, Weasel, Roslyn) | jobs are serialized expression trees: renaming a method or class breaks queued jobs; core is LGPL; slowest here | not a queue: no durable queue semantics without its schema; thread-pool style execution |

Why Quartz: the outbox already is the durable queue and owns retry/parking, so the library only has to run the worker in-process, wake the dispatcher, sweep periodically and later run cron jobs. Quartz does that fastest, with the smallest footprint and no extra schema or credential; Hangfire is the slowest and ties queued jobs to code shape; Wolverine overlaps with our own outbox (it has one) and brings a code-generation model we do not need yet. Wolverine is the better answer the day a broker replaces PostgreSQL, which is why the interface exists.

## Latency of a typical transaction (criterion: measure and record)
Method: test `LatencyBenchmarkIntegrationTests` (run with `OUTBOX_BENCH=1 OUTBOX_BENCH_OUT=<file> dotnet test backend/tests/Outbox --filter Typical_transaction`), real PostgreSQL 17 in Docker Desktop (loopback port-forward, not a network), app credential, same hardware as above. 200 warm-up then 1500 timed iterations of each variant interleaved; median and p95 per run, 3 runs. EF Core context created per call (as in production use).

| unit of work | median | p95 |
|---|---|---|
| begin + `set_config` + insert + commit (no outbox) | 1.55 to 1.59 ms | 1.76 to 1.78 ms |
| the same + outbox write (`OutboxWriter`) | 3.15 to 3.24 ms | 4.01 to 4.11 ms |
| dispatch one event (claim, mark, commit; no consumers), one dispatcher | 2.47 to 2.56 ms mean | |

The outbox write adds about 1.6 ms median (EF context + `SaveChanges` + one extra insert in the same transaction, no extra round trip for the commit). It scales with latency to the database like the context statement does (ADR-002). If it ever matters, `Database.ExecuteSqlAsync` for the insert drops the EF overhead. **Staging measurement (spec 5.3 item 9) is pending**: it needs the staging environment of tickets 11/12; the local numbers above are loopback and are not a substitute. Throughput of a single dispatcher is about 400 events/s; scale by running several dispatchers (SKIP LOCKED) and by organization.

## Consequences
Easier: events can never be lost or announced for a rolled-back change; consumers and tests need no broker; the organization context in background work is explicit and enforced by RLS; swapping the worker library or moving to a broker touches one adapter.
Harder: delivery is at least once with no global ordering, so every consumer is idempotent and order-tolerant; every event type needs a stable name and JSON shape (never rename once published); the outbox and inbox tables need pruning (not built yet); one extra insert per unit of work that publishes.

## Decided, to build before the first production worker
Owner decisions (2026-11-02); none is built yet. They close F1, F2 and the pruning item below.
- **Parked events (closes F1): alert plus manual reprocess.** An event with `falhou_em` set raises an alert/metric; an operator requeues it by hand (clear `falhou_em`, reset `tentativas`). The list of parked events will be visible in the Control Tower later. No automatic requeue.
- **Organization discovery (closes F2, BUILT in plataforma-base ticket 07, migration 0011): a registry table of organization ids, not a BYPASSRLS role.** See "Organization discovery (built)" below. A role that bypasses RLS was rejected: it would be a standing hole in tenant isolation.
- **Retention job:** a background job prunes delivered outbox rows and inbox rows older than 30 days (per organization, inside its tenant context). Parked events are never pruned automatically.
- **Quartz** confirmed as the queue/worker library behind `IBackgroundJobs`.

## Organization discovery (built)
Plataforma base ticket 07 (spec 5.8), migration `0011_outbox_organizacoes_pendentes.sql`. Closes F2.
- **Table** `outbox_organizacoes_pendentes (organizacao_id PK, desde)`: only ids and an instant, no event data. Not a tenant table (no RLS; listed in the exempt set of `RlsArchitectureIntegrationTests`, like `identidade_emails`): the application role has NO privilege on it, only SECURITY DEFINER functions touch it.
- **Written in the event's own transaction** by an `AFTER INSERT` trigger on `outbox_eventos` (`outbox_registrar_pendente`, row from the insert, refuses an event outside the context). No code path that inserts an event can forget it, and a rollback of the event rolls back the registry row. The trigger's no-op `DO UPDATE` takes the row lock on purpose (see closing).
- **Read** by `outbox_organizacoes_pendentes(limit)` (`PendingOrganizationsDatabase` in Data, the only connection without context besides `PreAuthDatabase`): ids and instants, oldest first, limit clamped 1..1000, refuses to run inside an organization context. The events themselves are still read under RLS, one organization at a time (`OutboxSweeper` -> `OutboxDispatcher.DispatchAsync(org)`).
- **Closed** by `outbox_encerrar_pendencia()`, run by the sweeper in the organization's own transaction after draining it: locks its registry row (`FOR UPDATE`), THEN checks for pending events with a new statement, deletes only if none. A writer that inserted an event but has not committed holds the same row lock via the trigger, so the closer waits and sees the event; no orphan event, no lost registration.
- All functions: SECURITY DEFINER, `search_path = pg_catalog, pg_temp`, `PUBLIC` revoked, EXECUTE only for `flow4tech_app` (the trigger function for nobody), owner neither superuser nor BYPASSRLS. No BYPASSRLS role was created. The migration backfills organizations that already had pending events by toggling `NO FORCE`/`FORCE` on `outbox_eventos` inside its own transaction (the owner's DDL, nothing left bypassed).
- Consequences: one extra upsert (row lock) per event inserted; events of one transaction for the same organization hit the same row; an organization whose events keep failing stays registered until delivered or parked; parked events (`falhou_em`) do not keep it registered. Scheduling the sweep (Quartz recurring trigger + worker hosting) is still open below.

## Open items
Follow-ups required before any production worker (not built in ticket 08):
- **F1. Parked events are silent** (decided above, to build: alert plus manual reprocess). An event that reaches `MaxAttempts` gets `falhou_em` and is never selected again: nothing redelivers it, requeues it, or alerts anyone. Needs an alert/metric on `falhou_em IS NOT NULL`, and an operator action to requeue (clear `falhou_em`, reset `tentativas`).
- ~~**F2. Organization discovery**~~ CLOSED by ticket 07 (see "Organization discovery (built)").
- Outbox/inbox pruning (decided above, to build: 30-day retention job) and a dead-letter view for parked events (Control Tower, later).
- Quartz adapter in `src`, worker hosting in `src/Api` (a recurring `OutboxSweeper.SweepAsync`), and DI registration (`TenantDatabase`, `OutboxDispatcher`, `OutboxSweeper`, consumers; `OutboxDispatcher` has a single constructor with an optional parameter: register by factory); with them the Api Npgsql exemption (ADR-002) can go.
- Warm the `OutboxContext` EF model at host start with the other contexts (first query about 1 s, ADR-002).
- `ModuleGraph` change made for this ADR: `src/Infrastructure/Outbox` may use EF Core packages/namespaces (like a module Infrastructure layer) because it maps its own tables through `TenantDbContext`; Npgsql, `DbContext` setup and the tenant setting stay Data-only; module Infrastructure layers may reference Outbox like Data. Tests in `ModuleBoundaryTests`.

## Alternatives considered
- Wolverine's own EF Core outbox: couples event storage to its message store and conventions, away from our `TenantDatabase`/RLS model; kept as the broker-era option.
- Publishing directly to a broker/queue inside the request: loses the atomicity with the state change (dual write).
- Change data capture / logical replication: needs infrastructure beyond one PostgreSQL; revisit with Kafka.
- `LISTEN/NOTIFY` as the queue: not durable; usable later only as a wake-up hint.
- Hangfire as the library: see comparison (slowest, expression-serialized jobs, LGPL core).
