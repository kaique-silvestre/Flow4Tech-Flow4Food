# 11: Fechamento da 1A

**What to build:** Teste de ponta a ponta com dois tenants na rota autenticada, atualização do `CLAUDE.md`, ADRs, conferência da spec e do `openapi.json`.
**Blocked by:** 01 a 10
**Touches:** `CLAUDE.md`, `docs/**`, `.scratch/S-02-plataforma-base/**`
**Nature:** objective
**tdd-scope:** exempt
**Seams:** Contract (OpenAPI), RLS
**Status:** done-local (parcial: pipeline do GitHub nunca rodou, pendência do dono; critérios da spec com evidência ou marcados como parciais/pendentes)

- [~] Todos os critérios de aceite da seção 6 da spec marcados com evidência (feito em `spec.md` seção 6: 11 `[x]`, 5 `[~]`, 1 `[ ]` pendente do dono; cada um com arquivo/teste ou motivo. Nenhum foi rodado em Testcontainers/CI, só em PostgreSQL 16 local)
- [x] `CLAUDE.md` descreve a rota autenticada de ponta a ponta (seção "A rota autenticada de ponta a ponta"; prova em `DoisTenantsEndpointsTests`, 5 testes)
- [~] Pipeline verde (ou pendência do dono registrada se ainda não rodou no GitHub): NÃO verde, NÃO verificado. Pendência do dono registrada no `state.md`: o CI do GitHub nunca rodou; Railway, Vercel, segredos e proteção do `main` não estão configurados.

## Entregas do fechamento
- Teste de dois tenants na rota autenticada: `backend/tests/Flow4Tech.Tests/DoisTenantsEndpointsTests.cs` (nome com `Endpoints`: roda no job `integration-tests`). Login real em duas organizações; unidades, sessões, configuração (valor, histórico, restauração), perfis, convites, contas e auditoria; varredura de todas as tabelas com `organizacao_id` (descobertas pelo `information_schema`, sem lista a manter): linha de outra organização = 0 e leitura sem contexto = 0.
- ADRs: 011 (proteção de login), 012 (convites, recuperação, Google; a pré-autenticação por `SECURITY DEFINER` não cresceu), 013 (configurações em camadas); 006 atualizado (regras persistidas), 005 aponta para eles.
- Item herdado resolvido: regra de perfil com unidade inexistente (ou de outra organização) agora é `Invalido` (`GestaoDePerfis.UnitsExistAsync`, `IPerfilStore.UnidadesExistemAsync`), antes era 500 por chave estrangeira. Teste: `A_rule_for_a_unit_that_does_not_exist_here_is_invalid_not_a_server_error`.
- Contrato: `-p:GenerateContract=true` + `pnpm generate` não geram diferença; `pnpm -r typecheck` verde.

## Pendências registradas (não implementadas)
- Rotas HTTP para gerir perfis: `perfil:gerir`, `perfil:ver`, `permissao:atribuir` estão no catálogo e nenhuma rota as pede (o serviço `GestaoDePerfis` e o simulador existem e são testados).
- Dono: CI do GitHub nunca rodou; Railway, Vercel, segredos e proteção do `main` não configurados.
- `CA1515` no `dotnet format` (classes de teste públicas) segue; fixar o SDK em `global.json` não foi feito.
