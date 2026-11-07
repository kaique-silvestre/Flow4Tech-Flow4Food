# 01: Backend fixes from the independent reviews of tickets 06 and 07

**What to build:** Close the backend risks the independent reviews found in the receive/close and rounds code, without changing behavior the waiter relies on.
**Blocked by:** None
**Touches:** backend/src/Comandas/**, backend/src/Pagamentos/**, backend/tests/Flow4Tech.Tests/**
**Nature:** objective
**tdd-scope:** required
**Seams:** Real database (Testcontainers, app credential); Contract
**Status:** ready-for-agent

- [ ] Receive payment, close the comanda and remove the service fee apply the same visibility rule as reading (`comanda:ver-comandas-de-outros`): a cashier without it gets the same answer as for a comanda they cannot read; HTTP test with a cashier lacking the permission.
- [ ] Undo of a round no longer needs the exact current `versao`: a second device launching inside the undo window does not turn the sender's undo into 409; still refused after the window, for another user, or when an item of the round was cancelled; HTTP test.
- [ ] Removing the service fee publishes an outbox event (stable name, in the same unit of work) besides the audit record.
- [ ] Parallel requests with the same round id and with the same payment id (HTTP, real database) produce one round / one payment and an idempotent answer for the other.
- [ ] Closing a comanda with zero consumption is refused or explicitly allowed with a test and a decision note in the ticket (recorded, not silent).
- [ ] `dotnet test backend/Flow4Tech.sln` green; regenerated `openapi.json` and client committed if any route changed.
