# F4-0067: Pre-bill, receipt and 'bill requested'

**What to build:** The server produces the pre-bill and the receipt data (non-fiscal) and a 'bill requested' mark that puts the table in 'waiting for the bill'.
**Blocked by:** F4-0063
**Touches:** backend/src/Comandas/**, backend/src/Pagamentos/**, backend/src/Api/**, backend/tests/**
**Nature:** objective
**tdd-scope:** required
**Seams:** Domain (pure); Real database (Testcontainers, app credential); Contract; Two tenants over HTTP (`DoisTenantsEndpointsTests` extended)
**Status:** done

- [ ] Pre-bill route: items, frozen prices, discounts, fee, total, per split part when a split exists; second copy of the receipt for a closed comanda; no fiscal document and no printing.
- [ ] 'Bill requested' mark on an open comanda (idempotent, audited, event `comanda.conta-pedida`), cleared by closing or cancelling; the table map status uses it.
- [ ] Output is data (JSON) and a plain document the phone can share; no secret or personal data beyond what the comanda holds.
- [ ] Permission `comanda:reimprimir` for the second copy; contract regenerated; two-tenant test.
