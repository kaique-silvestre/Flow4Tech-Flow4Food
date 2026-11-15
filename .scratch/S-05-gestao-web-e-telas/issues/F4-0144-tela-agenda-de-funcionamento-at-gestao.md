# F4-0144: G7 tela: agenda de funcionamento at /gestao/agenda

**What to build:** block G of slice S-05 (see the spec, Solução e Decisões de implementação). Ticket de tela: só tela, sobre o backend já mergeado.
**Blocked by: F4-0143**
**Touches:** frontend/app/src/**, frontend/app/e2e/**
**Nature:** ui
**tdd-scope:** required
**Status:** ready-for-agent

- [ ] Critérios: ver o item correspondente da spec (critérios 11 a 14 e 1 a 10 conforme o G).
- [ ] Toda rota nova declara RequireSession e RequirePermission, audita na mesma unidade de trabalho e tem teste de caso permitido, negado e de outra organização.
