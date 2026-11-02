---
name: spec
description: "Turn the current grill conversation into a spec with a UI contract, decided seams and tdd-scope, ready for /tickets. Runs /brief inline when the feature has screens DESIGN.md does not cover."
disable-model-invocation: true
argument-hint: "[feature-slug] [--auto|--human]"
---

# /spec

Forked from Matt Pocock's `to-spec` (2026-09). No interview: synthesize what the grill already settled. Write the spec in Portuguese; it is the user's document. Do not edit code.

## 1. Ground

Read `docs/agents/issue-tracker.md`, `CONTEXT.md`, ADRs in the area, `docs/design/DESIGN.md` if present. Use the glossary vocabulary throughout. `graphify-out/graph.json` exists → `graphify query "<feature>" --budget 800` and `graphify path "<A>" "<B>"` for the modules involved; never load the graphify skill. Otherwise targeted grep.

## 2. Seams

Sketch the seams to test at. Existing seams first, highest possible, ideally one. New seam → propose it at the highest point and say why. Show the list in Portuguese and confirm with the user before writing. These become `## Seams` and are never re-decided downstream.

## 3. Screens

Feature has a rendered surface → list screens and, per screen, the ref images in `docs/design/refs/<slug>/` (none → say so; the user adds them or accepts authorial). Then:

- `DESIGN.md` missing, or lacks a section for this feature → read `~/.claude/skills/brief/SKILL.md` and follow it inline now, same mode (`--auto`/`--human`).
- Section exists → reference it.

## 4. Write

`.scratch/<slug>/spec.md`:

```markdown
# <Feature>

## Problema
<from the user's view>

## Solução
<from the user's view>

## User stories
1. Como <ator>, quero <feature>, para <benefício>
... (long, exhaustive)

## Decisões de implementação
<modules touched, interfaces changed, architecture, schema, API contracts, interactions.
No file paths, no code. Exception: a prototype-derived snippet that encodes a decision.>

## UI Contract            # only when screens exist
- registro: <from DESIGN.md>
- telas: <name → route → DESIGN.md section → refs → estados obrigatórios → fluxos críticos>
- fora do DESIGN.md: <any token or pattern this feature adds, already merged by /brief>

## Seams
<seam → o que se observa ali → prior art de teste no repo>
tdd-scope: required | not-applicable per area

## Decisões de teste
<what a good test is here: external behavior only; which modules; prior art>

## Critérios de aceite
<measurable, one per line; each will map to a ticket checkbox>

## Fora de escopo

## Notas
```

Done when every section is filled, seams are confirmed, and every screen names a DESIGN.md section. Report the path and `/tickets` as the next step.
