# F4-0151: G6 backend: promocoes e combos (regra persistida, avaliada no servidor, promo registrada no item, ADR-018 order)

**What to build:** block G of slice S-05 (see the spec, Solução e Decisões de implementação). Ticket de backend: backend primeiro, sem tela.
**Blocked by: F4-0150**
**Touches:** backend/src/**, backend/migrations/**, backend/tests/**
**Nature:** backend
**tdd-scope:** required
**Status:** done

- [x] Critérios: ver o item correspondente da spec (critérios 11 a 14 e 1 a 10 conforme o G).
- [x] Toda rota nova declara RequireSession e RequirePermission, audita na mesma unidade de trabalho e tem teste de caso permitido, negado e de outra organização.
