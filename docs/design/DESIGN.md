# Design

register: product
direction: Minimalist (+ Technical touches: tabular figures, controlled density, explicit states)
intensity: internal (transactional; identity comes from type, contrast, grid, data and states, never from effects)
stack: React + Vite SPA, TanStack Router/Query, Tailwind over a project design system (`frontend/design-system`), Radix primitives, React Hook Form + Zod; tests: vitest + testing-library (logic, state, components), Playwright (critical flows and screenshots). Source: `.scratch/novo-sistema/04-arquitetura.md` A11 (decided; existing stack wins).
viewports: 390x844 (primary: waiter phone), 360x740 (smallest supported), 1440x900 (web management, not used by the first slice)
base source: derived from the approved prototype `.scratch/novo-sistema/prototipos/index.html` (tokens, components, flows), brief mode `--auto` (2026-11-05). Final font and palette stay open until real use on a device (`05-ux-design` U8): treat token VALUES as provisional, token NAMES as stable.

## Thesis
A calm, neutral phone tool that disappears while a waiter works one-handed in a crowded room: big targets, numbers that line up, one blue action per screen, and no decoration between the waiter and the next item.

## Scene
A waiter standing between tables in a loud, dim-to-bright restaurant, phone in one hand and a tray in the other, glancing for under two seconds at a time: light and dark themes both ship, and every status carries a word or icon so it survives glare and low light.

## Tokens
All values below are OKLCH converted from the prototype hex values (`:root` and `[data-theme=dark]` in the prototype). Neutrals carry 0.005-0.04 chroma toward the brand hue (about 263).

color, light:
- bg `oklch(0.979 0.001 0)` (page), surface `oklch(1 0 0)` (cards, sheets), surface-2 `oklch(0.957 0.019 263)` (brand-soft: selected chip, highlighted row), hover `oklch(0.965 0.002 260)`
- ink `oklch(0.208 0.040 266)`, ink-muted `oklch(0.508 0.032 260)` (5.5:1 on bg, 5.8:1 on surface; the floor for any text), ink-faint `oklch(0.661 0.026 262)` (2.9:1: **decorative or disabled only, never text that carries information**)
- accent `oklch(0.542 0.180 263)`, accent-ink `oklch(1 0 0)` (5.2:1), border `oklch(0.928 0.006 265)`, border-strong `oklch(0.872 0.009 258)`
- semantic: success/open `oklch(0.524 0.086 170)` on soft `oklch(0.957 0.014 169)`; warning/waiting marker `oklch(0.674 0.133 77)`, warning TEXT `oklch(0.496 0.101 72)` on soft `oklch(0.960 0.028 87)` (5.5:1); error/alert `oklch(0.554 0.162 14)` on soft `oklch(0.962 0.013 6)` (4.6:1); info/closed `oklch(0.711 0.035 257)` with text `oklch(0.508 0.032 260)`
- scrim: ink at 45% opacity behind sheets.

color, dark (neutral grey, never brown):
- bg `oklch(0.177 0.009 264)`, surface `oklch(0.217 0.011 261)`, border `oklch(0.288 0.016 264)`, ink `oklch(0.967 0.003 0)` (17:1), ink-muted `oklch(0.737 0.022 264)` (8:1), accent `oklch(0.727 0.143 257)` with accent-ink `oklch(0.187 0.043 266)` (7.7:1), success `oklch(0.742 0.114 165)`, warning `oklch(0.774 0.130 81)`, error `oklch(0.725 0.145 12)`.
- scrim: black at 60%.

type:
- family: Geist (one family for titles, UI and numbers), fallback `system-ui, -apple-system, "Segoe UI", sans-serif`. Self-host the font files (no Google Fonts request at runtime: the app must load on a weak connection).
- `font-variant-numeric: tabular-nums` on every price, quantity, number and count; amounts always as `R$ 1.234,56`.
- scale (fixed rem, ratio about 1.125-1.2, base 16px): 0.8125 (meta), 0.875 (secondary), 1 (body), 1.0625-1.125 (buttons and list rows), 1.25 (sheet title), 1.5 (screen title), 2 (keypad entry and big total only). Weights 400, 500, 600, 700. Line heights 1.35 body, 1.15 numerals and titles.
- `text-wrap: balance` on titles; no display type above 2rem.

space: 4 / 8 / 12 / 16 / 24 / 32 / 48. Screen side gutter 16. Row padding 12-14 vertical. Vary rhythm: tight inside a row, 24 between groups.

radius: sm 12 (inputs inside sheets, small buttons), md 14 (buttons, fields), lg 16 (cards, grouped blocks), sheet 20 (top corners only), pill 999 (chips, tabs, status badges). Nothing at or above 24 on cards or inputs.

shadow: none on content. One level for a sheet over scrim: `0 -2px 8px` at 10% ink. Separation comes from 1px borders and the scrim, never both a border and a wide soft shadow on one element.

motion: 150-250ms, ease-out-expo (`cubic-bezier(0.16, 1, 0.3, 1)`). Sheets slide on transform; button press `scale(0.98)`; undo countdown is a linear bar. `prefers-reduced-motion`: sheets crossfade, no scale, countdown stays but without animation of position. No page-load choreography.

touch: minimum target 44px; primary buttons 52px high; keypad keys at least 64px high; quantity steppers 44x44 with 8px between them.

## Layout fingerprint
shell: single column, safe-area aware. Sticky top bar (back, breadcrumb "Comandas > Mesa 12", running total, state badge with elapsed time). Content scrolls. Sticky bottom action bar for the one primary action of the screen, above the bottom tab bar when there is one. Bottom sheets for short tasks (new comanda keypad, send round, close bill). Tab bar at the bottom filtered by permission; the first slice shows only what the profile can use (Comandas, Caixa).
composition: split working surface on the comanda screen: what is already on the bill on top (compact rows, 4 to 5 visible), the picker below (search, category tabs, frequent items) so the waiter never leaves the screen to add an item; the bill area shrinks as the picker is used.
rhythm: dense inside rows (quantity, name with small modifier line, price on one baseline), generous around the primary action; 1px dividers instead of cards for lists; cards only for the grouped "last time at this table" suggestion and the totals block.
responsive shift: 390 is the design size; 360 drops the third line of secondary text and keeps targets at 44px; above 480px the phone column is centered at 480px wide and the web management app takes over (not built in this slice).

## Signature
The **round bar** (barra de rodada): a sticky bar that appears the moment the first item is staged and reads "5 itens não enviados · R$ 96,50 · cozinha e bar só recebem ao enviar" with one accent button "Enviar". After sending, the same slot shows an 8-second **undo** countdown, and a persistent **pending-actions queue** chip when the connection drops ("Sem conexão. Nada se perde."). It is what separates "staged" from "sent" at a glance and carries the offline promise. It appears on the comanda screen only.

## Anti-references
- The prototype's "Agora" home: four metric cards under a hero revenue number (hero-metric template, identical card grid). Not part of the waiter slice.
- The floating dashed "configuration bubble" of the prototype: a design tool, never shipped.
- The shell of the old Flow4Tech system (sidebar, top bar, KPI strip, card grid) on a phone.
- A "bar" theme: wood, serif, cream or sand backgrounds, hand-drawn art.
- Status by color alone (a green or red dot without a word).
- A confirmation modal before every send (the decision is send-then-undo; the confirmation before sending stays an option, `05` U4).

## Explored and discarded
- **Wizard** (open comanda, pick table, add items, send, as four full screens): discarded; breaks the "first item in under 10 seconds, each next item in two taps" goal.
- **Side panel / large dialog over the list** for the comanda: discarded on a phone; the full screen with the split bill and picker won (the web keeps the panel question open, `05` U8).
- **Item grid of product tiles**: discarded in favor of a dense list with a big `+` and `×N` per row; a tile grid hides price and code and makes the frequent list slower to scan.
- Winner: full-screen comanda with split bill and picker, bottom sheets for short tasks, sticky round bar.

---

## Salão (mobile slice)
refs: docs/design/refs/salao/02.png, 03.png, 04.png, 05.png, 07.png, 12-offline.png, 13-fila.png (01.png only for tokens; not a screen of this slice)
structure:
- 02 (comanda): top bar with back, breadcrumb, total and state badge with elapsed time; bill rows (quantity, name, modifier line, "Cancelar", price); search field; category tabs (Frequentes first); "last time at this table" card with "Adicionar tudo"; frequent rows with `×N` and `+`; bottom bar "Repetir rodada" (accent) and "Fechar conta" (secondary).
- 03 (send round): bottom sheet with breadcrumb, title, one sentence about where it goes and the undo window, itemized list, "Voltar" and the accent "Enviar agora" (focus ring visible).
- 04 (new comanda): bottom sheet with a huge number entry, free-table chips, a 3x4 keypad (Apagar, 0, Cancelar), secondary "Dar um nome", accent "OK" disabled until there is a value.
- 05 (close bill): bottom sheet with consumption, service charge toggle, total, "receive" amount field, and one button per payment method. **Only the cash button exists in this slice**, plus the change calculation (see conflicts).
- 07 (dark, staged round): same layout, staged rows with steppers (− and +) in accent numerals, sticky round bar "N itens não enviados" with accent "Enviar".
- 12 and 13: offline banner above the bill ("Você pode seguir lançando. Nada se perde.") and the visible queue of pending actions.
skin: base tokens (the prototype already uses them). Deviations: none.
components:
- Top bar, bill row, staged row with stepper, product row (`×N`, `+`), search field, category tabs (Radix Tabs), suggestion card, status badge (marker + word; Radix none, project component), bottom sheet (Radix Dialog styled as a sheet, project component), keypad (project component, no native input), round bar (project component), undo toast (Radix Toast with action), offline banner and queue (project component), primary/secondary button, chip (project, from the design system package).
- States for every interactive one: default, hover (pointer only), focus-visible (3px accent ring, never clipped by a sheet), active (`scale(0.98)`), disabled (45% opacity plus the reason in text when it blocks the flow), loading (inline spinner inside the button only while a request is in flight; the staged item is shown immediately), error (inline message with a retry action).
- Status badge vocabulary: Aberta (success marker), Aguardando pagamento (warning marker), Parada há 1h 12min (alert marker), Fechada (neutral marker). Marker shape differs per status in addition to the word.
states:
- loading: skeleton rows for the list of comandas and for the picker; never a centered spinner.
- empty: list of comandas ("Nenhuma comanda aberta. Toque em Nova comanda."), search with no results ("Nada com 'xyz'. Tente o código do produto."), cash register closed ("Caixa fechado. Abra o caixa para receber.").
- error: load failure with a "Tentar novamente" banner; send failure keeps the round staged and shows what did not go.
- success: toast "Rodada enviada · Desfazer" for 8 seconds; closing the bill returns to the list with the comanda marked Fechada.
- feature-specific: offline (banner plus queue, items keep being staged and are sent in order when the connection returns), concurrency conflict (the comanda changed on another device: a non-blocking notice "Esta comanda foi alterada" and a refresh that keeps the staged items), no permission (the action is absent, not disabled, unless explaining it helps; a 403 shows "Você não tem permissão" without leaking why), temporary-password session (only the change-password screen is reachable).
flows:
1. Login (e-mail or operational user + password + organization), forced password change when temporary, unit selection when more than one.
2. Open the cash register (choose register, informed float), close it (count, result).
3. New comanda by keypad (table number or name) and first item in under 10 seconds.
4. Add items (`+`, `×N`, search, frequent, repeat last round), stage, send, undo within 8 seconds.
5. Cancel an item with a mandatory reason.
6. Close the bill: total with service charge, amount received in cash, change shown large, confirm; comanda becomes Fechada.
7. Lose and regain connection while staging and sending.
mobile: this slice IS mobile. Route `/salao`, installable PWA. At 360px secondary lines may truncate with an ellipsis but price and quantity never do.
conflicts:
- Prototype close sheet offers Pix, Crédito, Débito, Vale-refeição, "Pedir pagamento" and "Dividir igualmente": **base slice has cash only**; structure kept, the other buttons are not rendered (not shown as disabled), amount field gains the change line. They come back in Phase 2.8.
- Prototype tab bar (Agora, Comandas, Calendário, Avisos, Mais): the slice ships Comandas and Caixa only; the rest are not rendered.
- Prototype caixa on mobile is "Em breve" (`05` U18, point 7) but roadmap 1.12 requires open and close in the slice: **no reference image**; designed from the base tokens and the U18 rules (float is not a sale, blind count hides the expected cash, difference shown in words).
- No reference for login, unit selection, forced password change, comanda list, empty/skeleton/error states: **authorial mode** for these screens; the user can add refs under `docs/design/refs/salao/` or accept authorial.
- The prototype's "dar um nome" and number-only entry both exist; with `Usar mesas` off the field reads "Comanda 7", not "Mesa 7" (`05` U4).
- approved: pending (brief ran in `--auto`, no mock; approval comes from reviewing the screenshots of the first UI ticket).
