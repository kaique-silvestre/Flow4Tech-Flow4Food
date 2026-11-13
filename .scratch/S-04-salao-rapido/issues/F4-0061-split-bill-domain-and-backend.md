# F4-0061: Split the bill: five ways with deterministic cents

**What to build:** Given a comanda the server computes a split preview and records the chosen split: equal parts, by person/seat, by item, free values, by percent; shared charges follow the chosen rule.
**Blocked by:** F4-0058
**Touches:** backend/src/Comandas/Domain/**, backend/src/Comandas/Application/**, backend/src/Api/**, backend/tests/**
**Nature:** objective
**tdd-scope:** required
**Seams:** Domain (pure); Real database (Testcontainers, app credential); Contract; Two tenants over HTTP (`DoisTenantsEndpointsTests` extended)
**Status:** done

- [x] One pure function for equal split with the remainder cents going to the first parts in a stable order; property tests: sum of parts equals the total, no negative part, deterministic, parts differ by at most one cent.
- [x] By item / by seat (item has an optional seat), free values (must sum to the balance) and percent (must sum to 100, cents resolved by the same rule).
- [x] Preview route is read-only and idempotent; recording a split is idempotent by client id and stored as `divisoes` rows that later payments reference; changing the comanda after a split invalidates it (clear 409/422).
- [x] Permission `comanda:dividir`; audit; event `comanda.dividida`; contract regenerated.
- [x] Service fee and tip follow the chosen rule and are part of the sums.

## Closing note
- Migration 0028: `itens_comanda.assento` (1 to 99), `divisoes` and `divisao_partes` (append-only, RLS); the part number is what F4-0063 will reference.
- Decisions by the agent, to confirm with the owner: `comanda:dividir` goes to Waiter, Manager and Cashier; a comanda that already received a payment is not split (409 `valor-ja-recebido`); the tip is an optional request field, outside the balance; the preview is a POST because it carries the request.
- Full `Flow4Tech.Tests`: 720 of 721 on the last agent run; the failure was the load-sensitive latency test `SessoesEndpointsTests.The_whole_authenticated_request_path_latency_is_measured`, which passed alone. A first run also had a Testcontainers startup failure. Both look like load on the dev machine; CI is the check.
