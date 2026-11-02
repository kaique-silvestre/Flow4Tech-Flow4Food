# 006. Authorization: own rules-as-data engine behind a single decision interface (Cedar and Casbin measured, swappable)

- Status: accepted (spike ticket 09); open items for the owner listed at the end
- Date: 2026-11-02

## Context
Every route must ask one place whether `who` may do `resource:action` `where` given a `context` (03-seguranca S8: explicit deny > allow > default deny; limits judged on the REAL operation value, not what the client says). 04-arquitetura A14 left open whether to adopt Cedar, build, or mix. Three real cases decide it: (1) a waiter may give a discount up to X% by profile, above it is denied; (2) courtesies with a CUMULATIVE daily cap, where two concurrent requests must not jointly exceed it; (3) the same permission, a manager allowed only on unit 2 and denied on unit 1. Plus: explicit deny beats allow, default deny, unknown permission denies.

## Decision
1. **One interface, `IAuthorizationEngine.DecidirAsync(Subject who, string permission, Scope where, DecisionContext context)`** in `Flow4Tech.Kernel.Authorization`. Callers and tests depend only on it. A behavioural contract (`tests/Authorization/AuthorizationEngineContract.cs`: 46 tests including the profile x permission matrix) runs unchanged against every implementation.
2. **Build, do not adopt: the production engine is our own rules-as-data engine** (`Permissoes.Domain` pure `PolicyEvaluator` + `Permissoes.Application` `RulesAuthorizationEngine`). Rules are data (`Rule(effect, profile, permission, unit?, maxPercent?, maxValueCents?, dailyCapCents?)` over a catalog of known permissions). Cedar and Casbin.NET were adapted behind the same interface and pass the same contract, so choosing later is a swap, not a rewrite.
3. **Cumulative limits are owned by us in every option** (none of the three engines is stateful): the counter is a PostgreSQL tenant table (`limites_consumo`, migration 0003, RLS) updated by ONE atomic statement (`INSERT .. ON CONFLICT DO UPDATE .. WHERE total + amount <= cap`) in a `TenantDatabase` transaction. This is the "hybrid" part and it is the same whichever engine decides the rest.
4. ASP.NET integration is resource-based authorization (`AuthorizationHandler<PermissionRequirement, OperationFacts>` that calls the interface). Name-only `[Authorize(Policy=..)]` cannot see operation values and is not used for permissions with limits.

## Evidence (spike `backend/spikes/authz`, disposable; production tests in `backend/tests/Authorization`)
Hardware/method: Intel i7-9750H, 12 logical cores, 16 GB, macOS 26.7.1 x64, .NET 10.0.6 Release; decisions timed one at a time on one thread (2k warmup, 20k samples, `Stopwatch` per call), in-memory counter store, the standard 10-rule policy; PostgreSQL 17 in Docker Desktop over loopback. One run each (ordering and magnitude are reliable, exact values are not).

| | Own rules-as-data | Casbin.NET 2.21.3 | Cedar via MonoCloud.Cedar 0.1.0 (native Rust) |
|---|---|---|---|
| passes the 46-test contract (cases 1-3, deny/default/unknown, matrix) | yes | yes | yes |
| case 1/3 limit and unit rules | plain fields | matcher expression (`Convert.ToInt64(p.maxpct)`, string-typed policy columns) | `when { context.percentBp <= 1000 }`, unit as the resource entity, profile as parent entity |
| explicit deny across several profiles; a unit-scoped deny also matches a request with no unit (fail closed) | native in the evaluator | no multi-subject request: one enforce per profile, deny detected from the matched rows | native (`forbid` overrides `permit`) |
| cumulative daily cap | not in the engine; our store | not in the engine; our store | not in the engine (Cedar is stateless by design); our store |
| decision latency (mean / p99), case 1 allow | 1.8 us / 5.0 us | 5.5 us / 6.2 us | 1107 us / 1441 us |
| other decisions measured | 0.9 - 1.5 us | 4.7 - 11.0 us (3-profile deny 11 us) | 1101 - 1114 us (flat: fixed cost per call, not rule work) |
| unknown permission | catalog check in the engine | catalog check in the adapter | catalog check in the adapter (no schema was written) |
| ASP.NET policy integration | one handler (identical for all three; `AspNetIntegrationTests`) | same | same |
| profile x permission matrix testing | table-driven `Theory`, plus a test that fails when a profile x catalog pair is missing | same suite | same suite |
| dependency risk | none | managed, community-maintained, 2.7 M downloads; matcher strings are evaluated expressions (must never come from tenant-editable text) | 0.1.0 published 2026-05-13, thin fork of cedar-java by one vendor, 3-4 MB native library per RID (6 RIDs shipped), bus factor 1 for the binding. The other .NET binding found, `CedarDotNet` 0.0.8, ships natives for linux-x64/win-x64 only (no macOS developers); not run. |

Cumulative cap on PostgreSQL under RLS, app credential, through the interface (`CumulativeLimitConcurrencyIntegrationTests`): 40 concurrent requests of R$ 1,00 against a R$ 10,00 cap allow exactly 10 and persist exactly 1000 cents, on 3 consecutive fresh days (the "row does not exist yet" insert race included); 40 concurrent mixed amounts keep the persisted total equal to the sum of the allowed ones and within the cap; per-organization isolation holds with the same user id. The same tests go red when the counter is replaced by read-then-write without a lock (40 allowed instead of 10; 12 instead of 10 in the per-organization test; the mixed-amount test let 6980 cents through a R$ 10,00 cap while only 76 were persisted, lost updates), and a permanent test keeps that unlocked store as a test double to prove the harness can fail. Cost: a capped decision is one transaction, 1.8 ms sequential (mean, including opening the tenant transaction and an EF context), about 870-950 decisions/s with 40 pooled connections, also when all callers hit the same counter row.

## Why build
- Expressiveness for what we need is equal: all three express per-operation percent/value limits and unit scoping; none expresses the cumulative limit, so the decisive piece is ours regardless. Choosing Cedar buys a policy language, schema validation and formal analysis we do not use yet, at the price of a translation layer, a pre-1.0 single-vendor binding and about 1 ms per decision (about 1000x the own engine; fine for a screen, wasteful for list endpoints that filter many rows).
- The own engine is about 150 lines, pure (no I/O), trivial to test exhaustively, and the rules stay data we can persist per organization later.
- Because the interface and contract are fixed, the cost of being wrong is one adapter plus a data migration of the rules.

## Revisit triggers (move to Cedar, or Casbin, behind the same interface)
Customers or support need to AUTHOR policies in a language (not pick profiles and limits); relationships/hierarchies (group inside group) that the flat profile model cannot express; a requirement for formal policy analysis; a Cedar .NET binding with a maintained, cached policy set and CI-proven macOS/Linux/Windows natives and a measured per-decision cost near 50 us.

## Consequences
- Easier: one door for every authorization question; engine swap is local; matrix coverage is enforced by a test; the cap cannot be overshot under concurrency (proved), but the budget is a reservation, not a transaction: it can be LOST (reserved, then the business operation fails or the response is lost) and DOUBLE-COUNTED (the caller retries after a failure and reserves again). There is no release path.
- Harder / accepted: we maintain the rules model; an Allow on a capped permission has already reserved budget, so if the business operation then fails the reservation stays (conservative: it may deny a legitimate later request until the next day). Joining the business transaction needs the counter write to share the caller's `TenantTransaction`; the Kernel interface cannot name that type, so it needs an ambient unit-of-work seam (open item).
- **Ticket 10 (and any caller) must not build on this seam as if it were transactional.** Before a flow relies on a capped permission it needs either a release/compensation call (`ICumulativeLimitStore` has none today) or the counter write joined to the business `TenantTransaction`. Until then capped permissions suit only low-stakes, idempotent flows, and retries must be treated as new consumption.
- The rules are in memory (`PolicySet`) in this ticket. Loading them per organization and the admin UI belong to ticket 10; the evaluator already takes the `PolicySet` as input.

## Alternatives considered
- Cedar as the engine: viable and correct on all contract cases; rejected for now on latency (about 1.1 ms measured), maturity of the .NET binding and the extra translation layer, with the revisit triggers above. Tested only through generated policies and without a Cedar schema; native policy authoring and schema validation were not evaluated.
- Casbin.NET: 5-11 us and mature, but string-typed policy columns and expression matchers make numeric limits clumsy and unsafe if policy text is ever tenant-editable; no multi-subject deny without one enforce per profile.
- Advisory locks or `SELECT .. FOR UPDATE` before an application-side compare: workable, but two statements and a lock protocol to keep right; the single conditional upsert is simpler and proved.
- OPA/Rego, Amazon Verified Permissions: not attempted; a separate process or a network hop per decision contradicts the single-process, no-new-infrastructure foundation.

## Open items for the owner
1. Scope of the daily cap: per user (implemented), and the day boundary is the UTC date of the server clock (`IClock`, never caller data; `DecisionContext` carries no time); a unit-local business day (timezone, "day" ending at closing time) is not decided.
2. Reserve-then-fail behaviour above (separate transaction, no release, retries double count) versus a release path or joining the business transaction. Must be decided before ticket 10 relies on capped permissions.
3. Currency: limits compare integer cents; currency equality is not checked yet.
