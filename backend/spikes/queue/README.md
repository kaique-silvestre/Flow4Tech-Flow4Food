# DISPOSABLE SPIKE (ticket 08): Wolverine vs Hangfire vs Quartz behind our `IBackgroundJobs`

Not part of `Flow4Tech.sln`: no CI gate builds, formats or audits it. Delete the directory when Phase 1 starts; the decision lives in
`docs/adr/003-outbox-e-fila.md` and the numbers in `.scratch/fundacao-tecnica/findings/outbox.md`.

Each library is adapted to `IBackgroundJobs` (src/Infrastructure/Outbox) and run through the same scenarios against real PostgreSQL 17:
org context restored by the handler under RLS, retry of a failing handler, pickup latency (paced) and burst throughput.
Wolverine and Hangfire persist in PostgreSQL (`wolverine` / `hangfire` schemas, created by the library with the owner credential).
Quartz runs on its RAMJobStore: its durable ADO.NET store needs the library's own DDL scripts (not shipped in the NuGet package), which is part of the cost being compared.

Run (needs Docker):

    SPIKE_QUEUE_OUT=/tmp/queue.md dotnet test backend/spikes/queue/Queue.Spike.csproj
