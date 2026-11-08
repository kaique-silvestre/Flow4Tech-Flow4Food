# F4-0032: Screens: sign in, change temporary password, choose unit

**What to build:** On the phone a waiter signs in with operational user (or e-mail) and password; a temporary-password session lands on the password change; a user linked to several units chooses the active one. Failures never reveal which part was wrong.
**Blocked by:** F4-0031
**Touches:** frontend/app/src/features/entrar/**, frontend/app/src/features/conta/**, frontend/design-system/** (as needed), frontend/app e2e specs
**Nature:** ui
**tdd-scope:** required
**Seams:** UI in the browser
**Status:** done

- [x] Sign-in screen at `/salao/entrar` posts to the existing login route (header `X-F4T-Csrf: 1`, typed organization as `organizacaoDoLogin`); success keeps the access token in memory and relies on the `__Host-f4t_refresh` cookie; the session is refreshed from a single place.
- [x] Failure shows one generic message ('Usuário ou senha inválidos'); 429 shows the wait time from `Retry-After`; 428 shows the captcha-required state; no field reveals whether the user exists.
- [x] A temporary-password session goes straight to the password change screen and reaches nothing else until it succeeds; policy errors (min 10 characters) are shown inline.
- [x] A user with several units sees the unit chooser; with one unit it is active at login; with none it sees an explanatory message and a sign-out button.
- [x] All states in DESIGN.md exist for these screens (loading in the button, error, blocked, captcha) with Playwright screenshots at 390x844 and 360x740; no horizontal scroll; targets at least 44px; no ban of the frontend-craft list appears.
- [x] Playwright flow against the real API: sign in as the seeded waiter, change the temporary password, pick a unit, land on the empty comanda list.
