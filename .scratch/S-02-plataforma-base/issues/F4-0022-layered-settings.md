# F4-0022: Configurações em camadas

**What to build:** Opções declaradas no código, resolução sistema > organização > unidade com trava, histórico e restauração, vigência a partir de instante. Opções iniciais: tempos de sessão, política de bloqueio, fuso e virada do dia operacional. Spec 5.7.
**Blocked by:** F4-0014, F4-0016
**Touches:** `backend/src/Organizacao/**`, `backend/migrations/**`, `backend/tests/**`
**Nature:** objective
**tdd-scope:** required
**Seams:** RLS, Audit
**Status:** done-local, sem revisão independente (nuvem, PostgreSQL 16 local no lugar de Testcontainers; ver abaixo)

- [x] Herança, trava, histórico e restauração testados (`ConfiguracaoResolucaoTests` 10 sem banco; `ConfiguracaoIntegrationTests` com PostgreSQL: `The_value_comes_from_the_system_then_the_organization_then_the_unit`, `A_lock_in_the_organization_...`, `A_lock_scheduled_for_later_...`, `History_lists_who_when_and_what_and_a_restore_is_a_new_version_...`, `Restoring_an_unknown_version_...`, RLS entre inquilinos, histórico só de acréscimo (UPDATE/DELETE/TRUNCATE 42501), escritores simultâneos com versões distintas)
- [x] Mudança com efeito operacional vale a partir do instante definido e não retroage (`A_change_applies_from_its_instant_and_never_reaches_back`, `A_new_change_cancels_a_scheduled_one_...`, `An_instant_in_the_past_is_refused_and_an_option_that_needs_an_instant_demands_one`, CHECK `vigente_a_partir_de >= criado_em` no banco: `The_database_refuses_a_version_that_starts_before_it_was_written`; HTTP: `A_session_reads_its_clocks_..._a_change_does_not_reach_sessions_already_started`)
- [x] Alteração exige `organizacao:configurar` e é auditada (`Changing_needs_the_permission_and_a_denied_person_changes_and_audits_nothing`, `Every_change_and_restore_is_audited_with_the_value_before_and_after`, `A_failure_after_the_write_leaves_neither_version_nor_audit_record`; HTTP `The_settings_routes_need_organizacao_configurar_and_a_denied_change_is_audited`: 401/403 sem corpo e `acesso:negar` nas rotas de escrita; matriz de fábrica em `PoliticaDeFabricaTests`: dono tem, conta comum não)
- [x] Tempos de sessão do ticket 04 passam a ler daqui (`Sessoes` lê `sessao.inatividade-horas`/`sessao.absoluta-dias` por organização via `ISettings`; `Flow4Tech:Sessoes:*` continua como camada de sistema; `ConfiguracoesDeIdentidadeTests`; `SessoesEndpointsTests` 51/51). Limites de bloqueio do 08 também (`bloqueio.*`, camada de sistema apenas, por decisão abaixo)
- [x] Resolvedor do dia operacional continua com o padrão (`ConfiguredOperatingDayResolver` intocado; `The_operating_day_options_are_stored_but_the_resolver_keeps_reading_its_own_default`)

## O que foi feito

- Kernel `Settings`: `SettingDefinition`, `[SettingsCatalog]`, `ISettings`, regras de valor, catálogo coletado (`settings.catalog.txt`, teste de arquitetura: chave publicada nunca some). Migração `0014_configuracoes.sql` (`configuracoes_versoes`, RLS exata, só acréscimo por privilégio). `Configuracoes` (Organizacao.Application), `ResolvedorDeConfiguracoes` (`ISettings` real), `PostgresConfiguracoesStore`, rotas `GET /configuracoes`, `PUT /configuracoes/{chave}`, `GET .../historico`, `POST .../restaurar` (`openapi.json` e cliente TS regenerados; `pnpm -r typecheck` ok). Novo projeto `tests/Organizacao` + job `organizacao-tests` no `ci.yml`.
- Opções iniciais: `sessao.inatividade-horas`, `sessao.absoluta-dias` (organização), `sessao.intervalo-atividade-segundos` (só sistema), `bloqueio.*` x7 (só sistema), `dia-operacional.fuso` e `dia-operacional.virada` (organização e unidade, exigem instante de início).

## Decisões e limites (premissas minhas)

- **Política de bloqueio só na camada de sistema**: o limitador julga antes de qualquer consulta (a organização de uma conta com e-mail ainda é desconhecida) e o endereço não tem organização; um valor por organização nunca seria lido. Declarado `SomenteSistema`; a configuração (variáveis antigas) é o padrão de sistema.
- Relógio da sessão por organização apenas (a spec 5.3.4 fala em "por perfil": camada de perfil fica fora deste ticket). O limite absoluto é gravado quando a sessão começa; mudar o valor só alcança sessões novas. A janela de inatividade é lida quando a sessão a desliza (1 vez por intervalo por sessão), sem leitura extra por requisição. Sem cache.
- Camada de unidade = unidade ATIVA da sessão (o cliente nunca informa unidade). Rotas exigem sessão com unidade ativa.
- "Versão vigente" = maior número de versão entre as já iniciadas; escrever uma versão nova cancela uma agendada que ainda não começou. Valor nulo = voltar a herdar; trava só na camada da organização e preserva-se quando a mudança não diz nada sobre ela.
- Duração de credencial temporária, convite e recuperação continuam em `IdentidadeOptions` (não eram opções iniciais da spec).

## Verificação (nuvem; fixture trocado TEMPORARIAMENTE por PostgreSQL 16 local, NÃO commitado)

Rodou: `dotnet build` limpo, `dotnet format --verify-no-changes` (só CA1515), Architecture 127/127, Organizacao 47/47 (inclui 20 sem banco), Identidade 346/346 + 5 novos sem banco, Isolation 49/49 (RLS exata da tabela nova, deriva do mapeamento), Audit 143/143, Authorization 204/204, Outbox 31 (+1 ignorado), `SessoesEndpointsTests` 51/51 (3 novos; classe de teste trocada temporariamente para o PostgreSQL local e revertida), Flow4Tech.Tests unitários 37/37. NÃO rodou: Testcontainers de verdade (Docker/CI), `PermissoesEndpointsTests`, `RequestContextEndpointsTests`, `DeployMigrationEndpointsTests`, `HealthEndpointsTests`, `PortBindingIntegrationTests`, job `openapi-contract` (oasdiff), PostgreSQL 17 (aqui 16; o índice usa `NULLS NOT DISTINCT`, PG 15+).
