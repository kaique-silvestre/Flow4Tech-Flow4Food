# F4-0145: G3 backend: estorno total e parcial (negative immutable entry, reason, cash shift, limit by GrantGuard) and cancelamento with payment

**What to build:** block G of slice S-05 (see the spec, Solução e Decisões de implementação). Ticket de backend: backend primeiro, sem tela.
**Blocked by: F4-0144**
**Touches:** backend/src/**, backend/migrations/**, backend/tests/**
**Nature:** backend
**tdd-scope:** required
**Status:** done

- [ ] Critérios: ver o item correspondente da spec (critérios 11 a 14 e 1 a 10 conforme o G).
- [ ] Toda rota nova declara RequireSession e RequirePermission, audita na mesma unidade de trabalho e tem teste de caso permitido, negado e de outra organização.
