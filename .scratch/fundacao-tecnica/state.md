base: refactor-v2 @ 06eb761 (work branch: fundacao-tecnica)
mode: auto   max_parallel: 1 (no worktrees, sequential)
wave: 1
tickets:
- 01 · repo-skeleton · done · rounds: 1 · PASS (notes: editorconfig rule opt-outs, pnpm-lock outside touches)
- 02 · local-env-and-health · done · rounds: 1 · PASS WITH RISKS (Dockerfile ignores Railway PORT: handle in 11)
- 03 · module-boundary-tests · done · rounds: 1 · PASS WITH RISKS (source-text regex bypass; NetArchTest later)
- 04 · ci-pipeline · done-local · rounds: 1 (+fix) · PASS WITH RISKS · PENDING-OWNER: first run, red-gate PR evidence, branch protection, measured duration
- 05 · openapi-ts-client · done-local · rounds: 2 · PASS WITH RISKS · PENDING-OWNER: first GitHub run of openapi-contract, throwaway PR with deliberate break
- 06 · rls-isolation-spike · done · rounds: 3 · PASS (note: no-context write asserted on one table). STOPPED for owner review
- 07 · data-access-choice · done · rounds: 3 · EF Core (owner decision); review PASS WITH RISKS, fixes 1-2 applied without a third review; own MigrationRunner kept w/ checksums
- 08..12 · pending
lessons: 0
