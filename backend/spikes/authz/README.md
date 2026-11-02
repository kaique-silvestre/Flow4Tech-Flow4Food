# DISPOSABLE SPIKE (ticket 09): Cedar and Casbin.NET behind the single decision interface

Not part of `Flow4Tech.sln`: no CI gate builds, formats or audits it. Delete the directory when Phase 1 starts; the decision lives in
`docs/adr/006-motor-de-autorizacao.md`.

What is here:
- `CedarAuthorizationEngine` (MonoCloud.Cedar, native Rust engine) and `CasbinAuthorizationEngine` (Casbin.NET): both implement
  `IAuthorizationEngine` from `Flow4Tech.Kernel.Authorization`, translating the same rules-as-data `PolicySet`.
- `EnginesContractTests`: runs the production contract (`tests/Authorization/AuthorizationEngineContract.cs`, compiled in) against both.
- `AspNetIntegrationTests`: ASP.NET Core resource-based authorization handler calling the interface.
- `DecisionBenchmark` (opt-in): decision latency per engine + PostgreSQL-backed cumulative cap.

Run (Docker needed for the benchmark class only):

    dotnet test backend/spikes/authz/Authz.Spike.csproj
    SPIKE_BENCH=1 SPIKE_BENCH_OUT=/tmp/authz-bench.txt dotnet test -c Release backend/spikes/authz/Authz.Spike.csproj --filter Benchmark
