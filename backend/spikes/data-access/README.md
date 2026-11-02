# DISPOSABLE SPIKE (ticket 07): Dapper vs EF Core vs raw Npgsql, and migration runner comparison

Not part of `Flow4Tech.sln`: no CI gate builds, formats or audits it. Delete the whole directory when Phase 1 starts;
the decision lives in `docs/adr/002-postgresql-com-rls.md` and the numbers in `.scratch/fundacao-tecnica/findings/data-access.md`.

Run (needs Docker):

    dotnet test backend/spikes/data-access/DataAccess.Spike.csproj
    SPIKE_BENCH=1 SPIKE_IMPL=dapper SPIKE_BENCH_OUT=/tmp/bench.txt dotnet test backend/spikes/data-access/DataAccess.Spike.csproj --filter Benchmark   # impl: raw | dapper | ef
    SPIKE_FLYWAY=1 dotnet test backend/spikes/data-access/DataAccess.Spike.csproj --filter Flyway   # needs the flyway/flyway image
