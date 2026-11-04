# 04: Sessões e tokens

**What to build:** Token de acesso curto com audiência, refresh rotativo com detecção de reuso, versão de sessão, revogação imediata, lista e encerramento de sessões, cookies seguros. Spec 5.3.
**Blocked by:** 03
**Touches:** `backend/src/Api/**`, módulo de identidade, `backend/migrations/**`, `docs/adr/**`
**Nature:** objective
**tdd-scope:** required
**Seams:** Contract (OpenAPI), RLS
**Status:** in-progress (rascunho WIP em cf358cf; NÃO validado: projeto Identidade com 28 falhas em 210 por esgotamento de conexões do PostgreSQL de teste, ver state.md)

- [x] Reuso de refresh derruba a cadeia da sessão (`SessoesIntegrationTests.Presenting_a_used_refresh_again_revokes_the_whole_family...`, `...Two_simultaneous_refreshes...`, HTTP `Replaying_a_used_refresh_cookie_kills_the_session...`)
- [ ] Desativar usuário, trocar senha e mudar permissão invalidam sessões na próxima requisição (PARCIAL: provados desativar, trocar senha, mudar flag de dono, remover vínculo da unidade ativa e revogar, um teste por gatilho; **mudar permissão/perfil não existe ainda** (tickets 05/06) e deve chamar a mesma mecânica; fica aberto até lá)
- [x] Token de um realm é recusado no outro (`TokenDeAcessoTests.A_control_token_is_refused_by_the_app...`, `...The_audience_alone_separates_the_realms...`, HTTP `Tokens_of_the_other_realm...`)
- [x] Tempos de sessão configuráveis (valores padrão até o ticket 10) (`Flow4Tech:Sessoes:InatividadeHoras/AbsolutaDias`, testes `The_clocks_are_configurable_by_option`, `The_session_ends_after_the_configured_inactivity...`)
- [x] Chaves de assinatura vêm de variável de ambiente, rotacionáveis (`Flow4Tech:Tokens:AppKey/AppKeyPrevious`; `Rotation_new_tokens_use_the_current_key...`; boot recusado sem chave em Production/Staging, testes de host e de processo)
- [x] Latência da checagem de revogação medida e registrada (ADR-010: p50 ~4 ms, p95 ~6 ms por checagem; requisição inteira p50 ~5 ms; testes `*_latency_is_measured`)

## Riscos e pendências
- **Mudança de permissão/perfil não invalida sessões ainda** (não há permissão mutável): os tickets 05/06 precisam incrementar `versao_credencial` ou chamar `ISessaoStore.RevogarOutrasDaContaAsync` ao mudar permissões/perfil/escopo (ADR-010, item 11).
- **Rotação concorrente estrita**: duas renovações simultâneas com o mesmo refresh derrubam a sessão. O front precisa renovar de um único lugar por vez (promessa única compartilhada/lock entre abas). Decisão do ADR-010, item 6.
- **Cookie `__Host-` com `Path=/`**: o pedido de restringir o path às rotas de refresh/logout é incompatível com o prefixo; escolhido o prefixo. Front e API precisam ser same-site (domínio próprio ou proxy), senão o navegador não envia o cookie `SameSite=Strict`; ainda não verificado contra a topologia real Vercel/Railway.
- **`/login` exige o cabeçalho `X-F4T-Csrf: 1`** (e Origin/Sec-Fetch-Site coerentes); o front (ticket de UI) tem de enviá-lo em login, refresh e logout. Origens extras em `Flow4Tech__Sessoes__AllowedOrigins__N`.
- IP da sessão = endereço do par; atrás do proxy do Railway será o do proxy até haver configuração de proxy confiável (deploy/ticket 08). Nada de `X-Forwarded-For` (controlado pelo cliente).
- Linhas de `sessao_refresh` só são podadas na rotação da própria sessão; sessões abandonadas/vencidas ficam até um job de limpeza recorrente (não existe; o gatilho recorrente da auditoria também não).
- `revogada_em` do gatilho de vínculo removido usa o relógio do banco (única exceção a `IClock`, informativa).
- Login e troca de senha seguem sem throttling (ticket 08); a auditoria `sessao:iniciar` serializa na cadeia da organização (o lock da cadeia é por organização): custo de contenção em picos de login.
- Rota de negócio com sessão sem unidade ativa agora responde **403** (antes 401): decisão do ticket (o cliente deve abrir a seleção de unidade, renovar não ajuda).
- A permissão `sessao:revogar`/`sessao:ver-todas` está só declarada no catálogo de rotas; hoje quem decide é "ser dono" (`Conta.Dono`); o ticket 05 deve substituir por `DecidirAsync` e separar "revogar a própria" (sempre permitido) de "revogar de terceiro" (a permissão), hoje numa só rota.
- Não provado: comportamento atrás de TLS/proxy real, cookie em navegador real (só o `Set-Cookie` e a repetição manual), carga concorrente de login (Argon2 de 46 MiB por tentativa), expiração por relógio real (usa `IClock` falso).

## Estado verificado em 2026-11-04 (rascunho WIP)

- Build limpo. Architecture 118, Flow4Tech.Tests 118, Isolation 49, Authorization 94, Audit 143, Outbox 18 verdes.
- Identidade: 28 falhas em 210, todas `53300 remaining connection slots are reserved`. A classe `GestaoDeContasIntegrationTests` passa isolada (20/20). Defeito de infraestrutura de teste a corrigir ao retomar; causa ainda não investigada.
- Nenhum critério marcado: faltam suíte verde, rodada adversarial e revisão independente. Detalhes e plano de retomada em `../state.md`.
