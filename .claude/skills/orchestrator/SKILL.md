---
name: orchestrator
description: "Run a ticket package to done: validates the package, ensures docs/agents/project.md, spawns the orchestrator agent, relays its Portuguese report."
disable-model-invocation: true
argument-hint: "<.scratch/<slug>> [--auto|--human] [--max-parallel N] [--resume] | --direct \"<small change>\" | --bugfix \"<symptom>\""
---

# /orchestrator

Thin wrapper. The `orchestrator` agent does the work; this skill only prepares inputs and relays the result. Talk to the user in Portuguese.

## 1. Parse

Arguments: package path (default: the only `.scratch/<slug>/` with an `issues/` dir; several → ask), `--auto` (default) or `--human`, `--max-parallel N` (default 3), `--resume`, or one of `--direct "<text>"` / `--bugfix "<text>"`.

## 2. project.md

`docs/agents/project.md` missing → detect from the repo and show the draft, then write after the user confirms:

```
dev: <cmd>            port: <n>
lint: <cmd>           format: <cmd>
typecheck: <cmd>
test: <cmd>           test-file: <cmd> <path>
e2e: <cmd | none>
package-manager: pnpm | npm | yarn
design: docs/design/DESIGN.md   refs: docs/design/refs/
viewports: 1440x900, 390x844
state-switch: query | hash | none   # how the app exposes ?state=empty for screenshots
max-parallel: 3
```

Detection: `package.json` scripts, `biome.json` / `eslint.config.*` / `.prettierrc`, `vitest.config.*` / `jest.config.*` / `playwright.config.*`, lockfile. No linter → write `lint: none` and say so once. Exists → read, do not re-detect.

## 3. Package check

For every `issues/NN-*.md`: has `Nature:`, `Touches:`, `Blocked by:`, `tdd-scope:`, and a matching `packets/NN.md`. `Nature: ui` also needs a `design:` block in the packet and `docs/design/DESIGN.md` with the named section. Any miss → list them and stop; the fix is `/tickets` (or `/brief`), not this skill.

`--direct`: write `.scratch/direct-<date>/issues/01-direct.md` + packet from the text (nature by content, touches from a quick grep, `tdd-scope: required` unless docs/config only), skip `/spec`. `--bugfix`: call the `diagnosing-bugs` skill first, then write `01-bugfix.md` whose first acceptance criterion is "a test reproduces the bug and fails", packet includes current vs expected behavior and the condition `C(X)` under which it appears.

Working tree must be clean (`git status --porcelain` empty) unless `--resume`. Dirty → stop and say what is uncommitted.

## 4. Spawn

`Agent` with `subagent_type: orchestrator` and a message carrying: package path, mode, max_parallel, resume, project.md path, base branch. Wait for it. Do not poll, do not intervene, do not answer questions on its behalf.

## 5. Relay

Print the agent's `## Resultado` table verbatim. Then, if any ticket is blocked, one line per blocked ticket in Portuguese: what a human should decide. Nothing else.
