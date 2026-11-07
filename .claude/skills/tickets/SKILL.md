---
name: tickets
description: "Break a spec into tracer-bullet tickets for /orchestrator: blocked_by, touches, nature, tdd-scope, seams, and one self-contained packet per ticket. Publishes to the tracker only on explicit yes."
disable-model-invocation: true
argument-hint: "[.scratch/<slug>/spec.md]"
---

# /tickets

Forked from Matt Pocock's `to-tickets` (2026-09). Same vertical-slice discipline, plus everything the `orchestrator` agent needs so implementers never rediscover the repo. Talk to the user in Portuguese; write tickets and packets in English.

## 1. Gather

Spec from the argument or the only `.scratch/<slug>/spec.md`. Read it whole: user stories, `## UI Contract`, `## Seams`, `## Testing`, `## Out of scope`. Read `docs/agents/issue-tracker.md`, `docs/agents/project.md` (if missing, `/orchestrator` creates it; note it), `CONTEXT.md` and ADRs in the area if they exist. `graphify-out/graph.json` exists → `graphify query "<feature>" --budget 800` once for the dependency picture; never load the graphify skill.

## 2. Slice

Tracer bullets: each ticket cuts a narrow but complete path through every layer it needs, is demoable alone, fits one fresh context. Prefactoring first. Wide mechanical refactors → expand / migrate-in-batches / contract, each batch its own ticket.

Per ticket decide:
- **blocked_by**: only tickets that genuinely gate it.
- **touches**: files/modules it will change. Explicit paths or globs. Two tickets in one wave sharing a path is allowed but flagged.
- **nature**: `objective` (API, logic, data, CLI) or `ui` (anything with a rendered surface the user sees). A slice with both: majority UI → `ui`, whole slice; heavy both sides → split into an `objective` ticket that blocks a `ui` ticket.
- **tdd-scope**: `required` or `not-applicable` (docs, config without behavior, generated code). Never `auto`.
- **seams**: the spec's seams this ticket tests at. Verbatim from `## Seams`.
- **ui**: `nature: ui` only. Screen(s), DESIGN.md section, ref image paths, required states, flows to screenshot, `state-switch` hint.

## 3. Quiz

Show the breakdown as a numbered list in Portuguese: title, blocked by, what it delivers, nature. Ask: granularity right? edges right? merge or split anything? Iterate until approved.

## 4. Write locally

Always. `.scratch/<slug>/issues/NN-<slug>.md`, numbered from `01` in dependency order:

```markdown
# NN: <Title>

**What to build:** <end-to-end behavior from the user's view>
**Blocked by:** NN, NN | None
**Touches:** <paths>
**Nature:** objective | ui
**tdd-scope:** required | not-applicable
**Seams:** <from spec>
**Status:** ready-for-agent

- [ ] <acceptance criterion, measurable>
- [ ] ...
```

And `.scratch/<slug>/packets/NN.md`, the only thing the implementer reads besides the files it edits:

```markdown
# Packet NN: <Title>
ticket: .scratch/<slug>/issues/NN-<slug>.md
worktree: <filled by orchestrator>

## Ticket
<the issue file body, verbatim>

## Spec excerpts
<only the stories, decisions and testing notes that bear on this ticket, quoted>

## Glossary
<domain terms used above, one line each, from CONTEXT.md or the spec>

## Seams
<seam → what to assert there → prior-art test file if one exists>

## Commands
<dev/lint/typecheck/test/test-file lines from project.md>

## design            # nature: ui only
section: <DESIGN.md section name>
refs: docs/design/refs/<feature>/01.png, 02.png
states: default, loading, empty, error
flows: <what to hover/focus/click for screenshots>
state-switch: query
```

Paths and code snippets stay out except a prototype-derived shape (state machine, schema) that encodes a decision.

## 5. Publish (ask)

Tracker in `issue-tracker.md` is GitHub/GitLab/other → ask in Portuguese: "Publicar N tickets em <tracker>? (sim/não)". Only on an explicit yes: one issue per ticket in dependency order, native blocking links where the platform has them, label `ready-for-agent`, and write the issue id back into the local file as `**Issue:** #NN`. Local tracker → nothing to publish.

Done when: every ticket file has every field, every ticket has a packet, and the user approved the breakdown. Report the package path and `/orchestrator <path>` as the next step.
