# 01: Repository skeleton

**What to build:** A fresh clone contains a modular .NET solution, pnpm workspaces for the two front apps and a shared design-system package, the Kernel module, empty `organizacao` and `permissoes` modules with the four internal layers, strict compiler/analyzer settings, a `CLAUDE.md`/`AGENTS.md` for agents and an ADR template. Nothing business-related exists yet.
**Blocked by:** None
**Touches:** `backend/**`, `frontend/**` (app, control, design-system), `pnpm-workspace.yaml`, `CLAUDE.md`, `AGENTS.md`, `docs/adr/**`, `.gitignore`
**Nature:** objective
**tdd-scope:** not-applicable
**Seams:** None (scaffolding; a smoke build/test only)
**Status:** done

- [ ] Solution builds and an empty test project runs green with nullable reference types enabled and analyzers set to strict
- [ ] `pnpm install` and a typecheck succeed for `app`, `control` and the design-system package with TypeScript strict mode
- [ ] Kernel module exists with placeholders for organization/unit context, money type (integer cents + currency), id type (UUID v7) and clock
- [ ] `organizacao` and `permissoes` modules exist, each with domain / application / infrastructure / interface layers, empty
- [ ] `CLAUDE.md` and `AGENTS.md` describe structure, build/test commands, module-boundary rules, money/time/id conventions and how to write a route the secure way
- [ ] `docs/adr/` has an ADR template
- [ ] `.gitignore` covers .NET and pnpm outputs in addition to the existing entries
