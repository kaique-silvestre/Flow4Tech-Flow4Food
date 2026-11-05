# Estado da execução: Fase 1A (plataforma base)

Atualizado em 2026-11-05. Branch de trabalho: `claude/flow4tech-fase-1-handoff-vgu3m9` (parte da `fundacao-tecnica` @ `972708a`, fast-forward). Spec: `spec.md`. Tickets: `issues/`. Nenhum PR aberto, nada em `main`.

## Placar

| # | Ticket | Estado | Commits | Revisão independente |
|---|---|---|---|---|
| 01 | Contexto de organização e unidade | **done-local** | `b784339`, `f10ed7a` | PASS COM RISCOS, achados médios corrigidos |
| 02 | Auditoria encadeada e log técnico | **done-local** | `dfcfdcd`, `f6ce74d` | PASS COM RISCOS, achados médios e 2 baixos corrigidos |
| 03 | Identidade, senhas e pré-autenticação sob RLS | **done-local** | `87d5ec5`, `8d9c698` | PASS COM RISCOS, achados corrigidos |
| 04 | Sessões e tokens | **done-local, sem revisão independente** | `cf358cf` (WIP), `281a20f`, correção de conexões (a commitar) | pendente |
| 05 | Permissões ligadas ao HTTP | **done-local, sem revisão independente** (cliente TS não regenerado) | a commitar | pendente |
| 06 | Perfis, regras e overrides persistidos, anti-autopromoção, simulador | **done-local, parcialmente verificado, sem revisão independente** | a commitar | pendente |
| 07 | Registro de organizações com eventos pendentes | **done-local, testes de PostgreSQL não rodados, sem revisão independente** | a commitar | pendente |
| 08 | Proteção de login e alertas | **done-local, testes de PostgreSQL/HTTP não rodados, sem revisão independente** | a commitar | pendente |
| 09 | Convites, recuperação de acesso, login com Google | **done-local, sem revisão independente** | a commitar | pendente |
| 10 a 11 | Configurações em camadas, fechamento | pending | | |

Último estado **validado** (rodado por mim, suíte inteira): `8d9c698`.

| Projeto de teste | Linha de base da Fase 0 | Em `8d9c698` |
|---|---|---|
| Architecture | 84 | 114 |
| Outbox | 18 (+1 ignorado) | 18 (+1 ignorado) |
| Authorization | 94 | 94 |
| Isolation | 43 | 49 |
| Flow4Tech.Tests | 50 | 72 |
| Audit (novo) | n/a | 143 |
| Identidade (novo) | n/a | 149 |

## Ticket 04: o que é verdade hoje (atualizado em 2026-11-04, retomada)

- Causa das 28 falhas: `PostgresFixture` sobe um PostgreSQL com `max_connections` padrão (100); os ambientes de teste de Identidade, em paralelo, esgotavam as conexões (`53300`). Correção: `-c max_connections=600` no container. Identidade agora **210/210**.
- Mesma execução: Isolation 49, Audit 143, Authorization 94, Outbox 18 (+1 ignorado), Flow4Tech.Tests 118, Architecture 118. Build limpo.
- Rodada adversarial por leitura de código e dos testes: sem achado novo (forja de token, reuso/corrida de refresh, CSRF, enumeração, vazamento, gatilhos de revogação, todos com teste).
- Critério em aberto por dependência: mudança de permissão/perfil invalidar sessões (tickets 05/06).
- **Falta o revisor independente** antes de tratar o 04 como fechado. O texto de sessões no `CLAUDE.md`/ADR-010 continua descrevendo o desenho; a medição de latência e a leitura acima o sustentam, mas o revisor ainda não o confrontou.
- Ticket 05 feito em seguida (ver `issues/05-permissions-http.md`). Em 2026-11-04: Flow4Tech.Tests 124, Architecture 125, Authorization 106, Identidade 210, Audit 143, todos verdes.
- Ticket 06 feito em 2026-11-04 (ver `issues/06-profiles-persisted.md`). Só `tests/Authorization` rodou (199/200 antes da última correção). `Architecture` rodou depois: 125/125 verde (achou e corrigiu: construtor ambíguo do motor na DI, `Npgsql` em Permissoes.Infrastructure, ordem do `audit.catalog.txt`, regenerado). `Isolation` rodou depois: 49/49 verde (RLS das 4 tabelas novas ok). `Flow4Tech.Tests` 123/124 (a falha, `Readiness_works_from_a_railway_style_DATABASE_URL`, deu ServiceUnavailable com a máquina saturada e passou isolada) e `dotnet format --verify-no-changes` limpo. Falta só a revisão independente do 06.
- Ticket 07 feito em 2026-11-05 (nuvem, sem Docker; ver `issues/07-event-org-registry.md`): migração 0011 (tabela sem RLS e sem privilégio para a aplicação, gatilho no evento, 3 funções SECURITY DEFINER), `PendingOrganizationsDatabase` (Data), `OutboxSweeper`, ADR-003 F2 fechado. Rodou: build, `dotnet format` (só CA1515), Architecture 125/125 e as funções SQL à mão num PG 16 local. NÃO rodou: Outbox, Isolation (exceção nova em `Exempt`), Identidade (teste do conjunto exato de SECURITY DEFINER atualizado), todos com Testcontainers. Rodar no Mac. Varredura ainda sem agendamento (precisa do worker Quartz).
- Ticket 08 feito em 2026-11-05 (nuvem, sem Docker; ver `issues/08-login-protection.md`): limitador progressivo por conta e IP em memória, credential stuffing, `ICaptcha` sem provedor, alerta de login novo (migração 0012 `acessos_conhecidos`), eventos `seguranca.login-novo|conta-bloqueada|senha-trocada` no outbox, consumidores de e-mail, `IEnviadorDeEmail` de desenvolvimento, 429/428 no `/login`. Rodou: build, `dotnet format` (só CA1515), Architecture 125/125 (catálogo de auditoria com `autenticacao:bloquear`), 40 testes unitários novos (Identidade, sem banco; 128 não-integração verdes), Flow4Tech.Tests unitários 37 verdes, SQL da 0012 à mão num PG 16, `openapi.json` e cliente TS regenerados (pnpm disponível aqui). NÃO rodou: `AlertasDeSegurancaIntegrationTests`, o teste 429 em `SessoesEndpointsTests`, Identidade/Isolation/Outbox inteiras (Testcontainers). Rodar no Mac. Recuperação sem enumeração fechada no 09. Próximo: ticket 10.
- Ticket 09 feito em 2026-11-05 (nuvem, sem Docker; ver `issues/09-invites-recovery-google.md`): migração 0013 (`convites`, `recuperacoes`, `identidades_externas`, sem função SECURITY DEFINER nova: o token leva a organização como localizador), convite de uso único (aceita com a senha da própria pessoa, e-mail só reservado ao aceitar), recuperação por e-mail sem enumeração (limitador do 08, piso de tempo, 202 sempre), credencial temporária nova para conta operacional (`usuario:redefinir-acesso`), Google OIDC (código + PKCE, `sub` + e-mail verificado, nunca cria conta, vínculo com senha atual + mesmo e-mail, operacional recusada no serviço e no banco), `openapi.json` e cliente TS regenerados. **Rodou**: build, `dotnet format` (só CA1515), Architecture 125/125, Flow4Tech.Tests unitários 37, Identidade sem banco (66 novos) e, contra um PostgreSQL 16 local com o fixture trocado TEMPORARIAMENTE (nada disso foi commitado): Identidade 346/346 (inclui as integrações do 08 e as 17 novas), Isolation 49/49, Audit 143/143, Authorization 203/203, Outbox 31 (+1 ignorado), `SessoesEndpointsTests` 48/48 (inclui o 429 do 08 e 5 testes HTTP novos). Achou e corrigiu: teste do 07 que supunha 1 item visível (o seed dá 2), `Program.cs` lendo `Bloqueio:*` cedo demais (teste 429 do 08 não passaria). NÃO rodou no Docker/CI: nada disso com Testcontainers de verdade (rodar no Mac), as outras classes `*Endpoints*` de Flow4Tech.Tests (Permissoes, RequestContext, Deploy), Google real. Pendências: aplicar perfil/unidade do convite ao aceitar (evento `identidade.convite-aceito` sem consumidor; depende do despachante e deve usar os direitos do convidador), liberação de e-mail após desativar, 500 em dois convites simultâneos ao mesmo e-mail, tempo real da recuperação não medido.

## O que foi entregue (tickets 01 a 03)

**01, contexto da requisição.** `RequestContextMiddleware` é o único lugar que monta `OrganizationContext` a partir da sessão; sem sessão 401, sem vínculo com a unidade 403. Módulo `Organizacao` com `usuario_unidades` (migração 0004, RLS exata), `GET /unidades`, `GET|POST /sessao/unidade-ativa`. Gate de arquitetura impede `organizacaoId`/`unidadeId` vindos do cliente (inclusive em genéricos, campos públicos e `[FromForm]`), e teste exige `.RequireSession()` em toda rota fora da allowlist.

**02, auditoria.** `auditoria_registros` (migração 0005) append-only por privilégio (a aplicação só tem SELECT e INSERT), hash SHA-256 encadeado por organização, escrita na mesma `IUnitOfWork` da ação, `RecordDetachedAsync` para falhas deliberadas, verificador de cadeia (`Altered`, `Removed`, `Reordered`, `Truncated`), âncora e estado de alerta em `auditoria_verificacao` (migração 0006), evento `auditoria.integridade-violada` emitido uma vez por violação distinta, regra única de segredos (`SensitiveData`), log técnico JSON com id de correlação W3C.

**03, identidade.** Módulo `Identidade`: `contas` e `credenciais` (migração 0007), Argon2id (Konscious 1.3.1), checagem de senha vazada por k-anonimato (HIBP desligada por padrão; lista local de 45 senhas), credencial temporária mostrada uma vez e com prazo de 72 h (migração 0008), login sem enumeração, fundador único por organização, regras do dono, pré-autenticação por funções `SECURITY DEFINER` sem papel que ignore RLS, tabela global `identidade_emails` como única exceção a `organizacao_id`. ADR-005.

Auditoria de segurança pelos revisores: nenhum achado alto nos três tickets.

## Decisões tomadas por mim (premissas; o dono não respondeu às questões abertas da spec)

- Uma conta pertence a **uma só** organização (questão 1). Se o dono quiser e-mail em duas organizações, muda o ticket 03 e o desenho de `identidade_emails`.
- Cópia externa da auditoria fica como **dívida** (questão 3).
- E-mail e CAPTCHA só como interface, sem provedor (questão 4).
- Conta operacional não usa Google (questão 6).
- Google segue **dentro** da Fase 1, por decisão explícita do dono (ticket 09).
- Fatiamento 1A/1B aprovado pelo dono.

## Pendências e dívidas conhecidas

Para tickets futuros (já anotadas nos tickets):
- 04: sessão sem unidade ativa; troca de unidade na mesma unidade de trabalho; limite de corpo na troca de senha; nenhum caminho de sessão de teste em Production.
- 05/06: autorização completa por `DecidirAsync`; gate "toda ação [A] tem registro de auditoria" (depende do catálogo [A]); `sessao:ver-todas`/`sessao:revogar` hoje só para donos.
- 08 (resolvido em parte): DoS de login coberto pelo bloqueio (tentativa bloqueada não audita nem faz hashing); ainda faltam throttling na troca de senha, estado do limitador compartilhado entre instâncias, `ProxyConfiavel` ligado no Railway, e-mail real, consumidores sem agendamento.
- 09 (resolvido em parte): reserva de e-mail sem prova de posse resolvida no convite (só reserva ao aceitar); redefinição de credencial vencida feita; ainda faltam a liberação do e-mail após desativar e aplicar perfil/unidade do convite; o cadastro do fundador só pode ser chamado por fluxo confiável (Control Tower, 1B), nunca como rota pública.
- Auditoria: job periódico de verificação existe e é testado, mas **nada o agenda**; evento de integridade sem consumidor; custo do lock da cadeia sob carga não medido; cópia externa pendente.
- Identidade: HIBP real nunca foi chamada (sem rede no ambiente); tempo uniforme do login só tem o piso testado (400 ms), não o tempo real; linhas anteriores à migração 0008 não têm prazo e nunca expiram.
- Gates `WithoutUnit`, `PreAuthDatabase` e `ClientContextInput` são convenção por nome/regex: barram o descuido, não o ataque.

Herdadas da Fase 0 (não mexidas): pipeline do GitHub **nunca rodou**; ticket 12 (reversão de deploy, latência em `staging`, ADRs finais) pulado por decisão do dono; Railway, Vercel, segredos, proteção do `main` e medição do pipeline continuam do lado do dono (P1 a P13 no ticket 11 da Fase 0); itens do outbox ainda não construídos (alerta e reprocesso de estacionados, poda de 30 dias; a descoberta de organizações pendentes é o ticket 07).

## Divergências de ambiente

- **Retomada no Windows (2026-11-04):** SDK 10.0.101, Docker Desktop. O clone tinha `core.autocrlf=true` (CRLF quebrava a comparação de `permissions.catalog.txt` e o `dotnet format`); passou a `false` (local) com checkout refeito. A pasta `backend/src/Api` estava como `api` no disco (resto do layout Python da `development`); renomeada. `ModuleGraph` agora ignora `obj`/`bin` com qualquer separador. Sugestão para o ticket 11: `.gitattributes` com `* text=auto eol=lf`. O `dotnet format` ainda acusa só `CA1515` (divergência de SDK já descrita abaixo).

- Esta sessão não tinha .NET: instalei `dotnet-sdk-10.0` e `postgresql` por `apt` e subi o Docker (`dockerd`) manualmente; a imagem `postgres:17-alpine` baixou normalmente. `dot.net` e `builds.dotnet.microsoft.com` são bloqueados pelo proxy; `apt` e NuGet funcionam.
- `dotnet format --verify-no-changes` acusa erros **CA1515** em classes de teste públicas (13 desde `8d9c698`, 11 já na linha de base, medidos com o SDK 10.0.114 daqui). A hipótese é divergência de versão do SDK em relação à máquina do dono, que reportou "limpo". Ao tornar a classe `internal`, o xUnit1000 exige que seja pública, então não dá para zerar por código. Resolver no ticket 11 (por exemplo fixando o SDK em `global.json`) e **não** suprimir regra.
- O `.claude/` não está no git (decisão do dono). Os comandos `/spec`, `/to-spec`, `/tickets` e o agente `orchestrator` **não existem** nesta sessão. A spec e os tickets foram escritos à mão no formato da Fase 0, e a execução foi feita por subagentes genéricos (implementador, revisor independente, corretor), sequencial, um commit por rodada.
- Em 2026-11-03 uma tentativa de correção do ticket 03 caiu num erro de limite de uso da conta antes de alterar arquivo; foi refeita e concluída (`8d9c698`).

## Processo (o que funcionou nesta execução)

Explorar, testes primeiro com evidência de vermelho, um commit por ticket, **eu conferir a suíte inteira por conta própria** antes de aceitar o relatório do agente, revisor independente novo a cada ticket, correção dos achados médios antes de seguir. Custo medido: 35 a 50 min por ticket com revisão e correção. O orquestrador anterior já tinha reportado "placeholder" e pulado exploração; aqui nenhum relatório foi aceito sem rodar os números.

## Questões abertas para o dono (spec seção 9)

1. E-mail em mais de uma organização? (premissa atual: não)
2. Confirmar a tabela-registro do dispatcher (ticket 07).
3. Cópia externa da auditoria só antes do primeiro cliente pago?
4. Provedor de e-mail e de CAPTCHA para `staging`.
5. Tempos padrão de sessão (hoje: 12 h de inatividade e 14 dias absoluto, provisórios no rascunho do 04).
6. Conta operacional nunca usa Google?
7. Identidade visual v6 ou v7 (entra na 1B).
