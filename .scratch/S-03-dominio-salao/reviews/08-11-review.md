# Review F4-0031..0034 (tickets 08-11): verdict PASS WITH RISKS

Read-only review of commits afadd9f, 92acfdf, 5431d44, 1d66918. Nothing was run; this is static review.

## Findings

- sev: risk · standards/security · frontend/app/src/Shell.tsx:42-46, frontend/app/src/features/conta/UnidadePage.tsx:~38, frontend/app/src/main.tsx:10 · Logout (`sair()`) clears the session state and the offline queue but NOT the TanStack Query cache. Keys `["permissoes", unit]`, `["comandas"]`, `["mesas"]`, `["caixa","atual"]` carry no account or unit. The next person on a shared phone, signing in to the same unit, sees the previous user's permissions (staleTime 60s, so tabs/actions are shown or hidden from the wrong profile) and the previous user's comandas and cash shift until the refetch lands. The server still decides, so it is a leak/UX problem, not an authorization bypass. · fix: pass `queryClient` to `sair()`/the `anonima` transition and call `queryClient.clear()` (also on `renovar` falling to `anonima`, and after `aceitarSessao`); or add account `contaDaSessao()` to every key. Add a test: sign out, sign in as someone else, no stale data. · fp: standards/sessao/query-cache-survives-logout

- sev: risk · spec · frontend/app/vercel.json:6 · `api` uses `baseUrl: window.location.origin`, but production `vercel.json` has only the SPA fallback rewrite (which also rewrites `/login`, `/comandas` ... to index.html). No rewrite/proxy to the Railway API exists, so production sign-in would receive HTML. Dev/preview/CI are proxied (vite.config.ts:8). The ticket text assumes "routed (production)" but nothing in the diff does it. · fix: add API rewrites (before the catch-all) to the staging/production API host, or set a `VITE_API_URL` base and CORS/cookie plan in ADR-009; at least document in the runbook. · fp: spec/app/vercel-api-routing

- sev: note · standards · frontend/app/src/features/caixa/CaixaPage.tsx:60,103 and comandas/consultas.ts:13,35 · `as unknown as Record<string, unknown>` casts then field reads: the generated client types for these routes are apparently untyped (responses missing from openapi). Typed safety is lost and mapping bugs surface at runtime only. · fix: add `.Produces<T>()` to those backend routes, regenerate openapi/client, drop the casts. · fp: standards/app/untyped-responses

- sev: note · standards · frontend/design-system/src/styles.css:527 (`.f4-badge` 28px) and app.css:239 (36px) · badges are not interactive, so below the 44px target is acceptable; confirm no badge is wrapped in a button/link. All interactive controls found use 44-56px. · fp: standards/ds/badge-height

- sev: note · frontend/app/src/features/sessao/sessao.ts:55-63 · A 401 on a non-public route triggers one shared `renovar()` and a single replay; the replay is not itself retried, so no loop. If refresh fails with a network error the state stays `ativa` with a dead token (only 401/403 move to `anonima`); user sees an error state, acceptable. · fp: standards/sessao/refresh-network-error

## Checked and fine
- Token: access token only in memory; refresh is an HttpOnly cookie (never read by JS). No localStorage use except the pending queue (with account tag; cleared on sign-out). `contaDaSessao` decodes `sub` only for queue ownership.
- Single refresh: `renovar()` shares one in-flight promise; `restaurar()` too. Public paths skip the replay.
- No client-supplied org/unit: only `organizacaoDoLogin` (typed login field, allowed by CLAUDE.md) and `unidadeDestinoId` (destination validated by server).
- Permission hiding: tabs/actions absent until `carregado`; 403 messages on every action; backend `GET /sessao/permissoes` is `.RequireSession()` + permission, read-only, filtered to salao prefixes, tested for garcom/supervisor/dono/anonymous. Server still enforces each route.
- PWA: no `runtimeCaching`; precache is static assets only; `navigateFallbackDenylist` covers API paths. API never cached by the SW.
- Generated client: `schema.ts` diff is additive and matches the new openapi operation (consistent with regenerate rule); no hand edit evident.
- Tests: behaviour-level (testing-library, Playwright against real API at 390x844 and 360x740, session tests for single-flight refresh). 
- CI: `frontend-tests` and `frontend-e2e` are added and wired into `gates` needs; pinned pnpm 11 / node 22, timeouts set, screenshots uploaded, stack torn down with `if: always()`. Sanity OK. Minor: `docker compose up --build` in e2e duplicates the image build with other jobs (time budget only).

verdict: PASS WITH RISKS
