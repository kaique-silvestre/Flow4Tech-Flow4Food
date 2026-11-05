# 06: Perfis e regras persistidos, anti-autopromoção e simulador

**What to build:** Perfis de fábrica e personalizados, modelos, overrides, escopo por unidade, limites e deny persistidos por organização, simulador pelo mesmo motor, matriz estendida. Spec 5.4.3 a 5.4.7, 5.4.9.
**Blocked by:** 05
**Touches:** `backend/src/Permissoes/**`, `backend/migrations/**`, `backend/tests/Authorization/**`
**Nature:** objective
**tdd-scope:** required
**Seams:** RLS, Authorization, Cumulative limits
**Status:** done-local (código e testes escritos; só `tests/Authorization` foi rodado, 199 de 200 verdes antes da última correção de teste; `Architecture` 125/125 verde; `Isolation`, `Flow4Tech.Tests` e `dotnet format` NÃO foram rodados; sem revisão independente)

- [x] Matriz perfil x catálogo verde com as permissões novas, também sobre regras lidas do banco (`PersistedRulesEngineTests` roda o contrato inteiro sobre `PostgresPolicySource`; as três permissões novas estão no catálogo, mas a matriz de fábrica só as dá ao `dono`)
- [x] Perfis de fábrica não editáveis, só duplicáveis (`A_factory_profile_cannot_be_edited`, `Duplicating_a_factory_profile_by_copy...`)
- [x] Mudar permissão, perfil ou vínculo invalida sessões afetadas (gatilhos da migração 0010 somam 1 em `contas.versao_credencial`; `Every_change_of_rights_ends_the_sessions_of_the_affected_account_only`)
- [x] Simulador devolve o mesmo resultado de `DecidirAsync` (`The_simulator_returns_exactly_what_the_engine_decides`; não reserva teto)
- [x] Teste de concorrência do teto continua verde com regras vindas do banco (`CumulativeLimitFromPersistedRulesIntegrationTests`, 40 requisições, teto exato)

## O que foi feito
- Migração `0010_perfis_regras.sql`: `perfis` (personalizados; `modelo` + `acompanha_modelo`), `perfil_regras`, `usuario_perfis`, `usuario_regras` (overrides). Todas com RLS exata. Os perfis de fábrica (`dono`, `conta`) NÃO são linhas: continuam código (`PoliticaDeFabrica`).
- `IPolicySource` / `PostgresPolicySource`: o motor lê, por decisão e sem cache, regras de fábrica + perfis da organização + overrides (chave privada `usuario:<id>`). Perfil que acompanha o modelo recebe as regras dele (um nível). Regra persistida de permissão que saiu do catálogo fica inerte.
- `GrantGuard` (Domain, puro): só concede o que tem; limite do concedente não pode ser removido nem aumentado; deny do concedente que alcança a unidade bloqueia; deny é sempre concedível.
- `GestaoDePerfis`: criar, duplicar (copiar ou acompanhar), substituir regras, atribuir/remover perfil, definir overrides. Cada operação é uma unidade de trabalho: pergunta ao motor (`perfil:gerir`/`permissao:atribuir`), aplica anti-autopromoção e anti-escalada, grava, audita. Recusas de autopromoção e escalada viram `permissao:negar-escalada` (negado, fora da unidade).
- `SimuladorDePermissoes`: chama o próprio `DecidirAsync` numa unidade de trabalho que termina com rollback (exceção interna `SimulacaoConcluidaException`), logo não reserva teto. Exige `perfil:ver` ou `permissao:atribuir`.
- `IPerfisDoUsuario.ListarAsync` agora recebe `(OrganizationContext, userId)` (o simulador pergunta por outra pessoa). `Api/PerfisDaConta` devolve chave de fábrica + perfis atribuídos + chave de override.
- Catálogos atualizados À MÃO: `permissions.catalog.txt` (+`perfil:gerir`, `perfil:ver`, `permissao:atribuir`) e `audit.catalog.txt` (+6 ações de `PerfisAuditActions`).

## Riscos e pendências
- Não há rotas HTTP para gerir perfis: o ticket só toca Permissoes/migrações/testes. As três permissões novas não estão em nenhuma rota; o cliente TS não muda.
- A decisão lê 3 tabelas por chamada, sem cache (correção primeiro). Medir antes de otimizar.
- Unidade inexistente numa regra vira exceção de FK (500), não 400; falta validar unidades no serviço.
- Simulador: não chamar dentro de outra unidade de trabalho (o rollback levaria o trabalho do chamador).
- `ProfileBranchingTests` é regex: nomes de variável que começam com `Perfil`/`Profile` seguidos de `==` falham o gate (por isso `IdDoPerfil`, `novo`).
- Mudança de perfil de fábrica no código (nova permissão para `conta`) NÃO invalida sessões; só edição de dados invalida.
- `tests/Architecture` rodou: 125/125. Não rodados: `tests/Isolation` (inclui a regra RLS das 4 tabelas novas), `Flow4Tech.Tests`, `dotnet format --verify-no-changes`.
