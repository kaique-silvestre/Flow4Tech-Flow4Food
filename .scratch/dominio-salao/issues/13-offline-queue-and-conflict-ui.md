# 13: Offline: pending-actions queue, reconnection, conflict notice

**What to build:** Losing the signal never loses an order: writes go to a visible queue, are retried in order when the connection returns without duplicating, and a comanda changed on another device shows a non-blocking notice that keeps what the waiter staged.
**Blocked by:** 12
**Touches:** frontend/app/src/features/fila/**, frontend/app/src/features/comandas/**, frontend/design-system/** (offline banner, queue), e2e specs
**Nature:** ui
**tdd-scope:** required
**Seams:** UI in the browser
**Status:** ready-for-agent

- [ ] Every write is queued on the client with its client-generated id (UUID v7), sent in order, retried with backoff and visible: banner 'Sem conexão. Você pode seguir lançando. Nada se perde.' and the queue list as in `12-offline.png` and `13-fila.png`.
- [ ] With the network cut in Playwright (browser context offline), the waiter keeps staging and sending rounds; when it returns the actions arrive in order and the server holds one round per id (no duplicate), checked through the API.
- [ ] A 409 shows 'Esta comanda foi alterada' without blocking, refreshes the comanda and keeps the staged items; a 401 sends the user to sign in again; a 403 shows 'Você não tem permissão' with no reason.
- [ ] The refresh token is renewed from a single place even with several queued requests (no parallel refresh).
- [ ] Screenshots of offline, queue with pending items, reconnection and conflict at 390x844 and 360x740; reduced-motion variants.
