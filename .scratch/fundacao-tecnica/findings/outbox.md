# Outbox and queue spike: raw numbers (ticket 08)

Hardware: Intel i7-9750H 2.6 GHz, 12 threads, 16 GB, macOS (Darwin 25.6), Docker Desktop PostgreSQL 17-alpine over loopback. Decision and analysis: docs/adr/003-outbox-e-fila.md.

## Typical transaction (backend/tests/Outbox, LatencyBenchmarkIntegrationTests; 200 warm-up, 1500 iterations, 3 runs)
| unit of work | median | p95 |
|---|---|---|
run 1:
| begin + set_config + insert + commit (no outbox) | 1,55 ms | 1,76 ms |
| begin + set_config + insert + outbox write + commit | 3,15 ms | 4,01 ms |
| dispatch one event (claim, mark, commit; no consumers), 1700 events | 2,47 ms mean | |
run 2:
| begin + set_config + insert + commit (no outbox) | 1,57 ms | 1,77 ms |
| begin + set_config + insert + outbox write + commit | 3,15 ms | 4,02 ms |
| dispatch one event (claim, mark, commit; no consumers), 1700 events | 2,56 ms mean | |
run 3:
| begin + set_config + insert + commit (no outbox) | 1,59 ms | 1,78 ms |
| begin + set_config + insert + outbox write + commit | 3,24 ms | 4,11 ms |
| dispatch one event (claim, mark, commit; no consumers), 1700 events | 2,53 ms mean | |

## Queue candidates (backend/spikes/queue, one run, 10 warm-up, 100 paced, 300 burst)
| candidate | pickup median | pickup p95 | burst |
|---|---|---|---|
| quartz | 3,5 ms | 4,6 ms | 1947 jobs/s |
| hangfire | 14,5 ms | 16,2 ms | 134 jobs/s |
| wolverine | 4,1 ms | 5,1 ms | 919 jobs/s |

Quartz ran on RAMJobStore, Wolverine and Hangfire on PostgreSQL persistence (Hangfire poll interval 100 ms, 4 workers).
Locale note: decimals print with a comma (pt-BR).

## Staging
Not measured: pending the staging environment (tickets 11/12).
