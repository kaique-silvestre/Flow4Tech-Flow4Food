# 08: Proteção de login e alertas de segurança

**What to build:** Bloqueio progressivo por conta e por IP, detecção de credential stuffing, interface de CAPTCHA, alerta de login novo, eventos de segurança no outbox, interface de e-mail com implementação de desenvolvimento. Spec 5.5.
**Blocked by:** 04, 07
**Touches:** módulo de identidade, `backend/src/Infrastructure/**`, `backend/tests/**`
**Nature:** objective
**tdd-scope:** required
**Seams:** Outbox, Audit
**Status:** done-local (unitários rodaram; integração e HTTP escritos e NÃO rodados: sem Docker; sem revisão independente)

- [x] Bloqueio progressivo por conta e por IP demonstrado em teste. Evidência: `LimitadorDeLoginTests` (15) e `AutenticacaoProtegidaTests` (12) rodaram, verdes, sem banco: limites distintos por tipo de conta, atraso dobrando até o teto, fim do bloqueio, janela, IP, credential stuffing nos dois sentidos, CAPTCHA adaptativo, memória limitada. Tentativa bloqueada não faz hashing. Rota (429 + `Retry-After`, igual para nome conhecido e inexistente): `SessoesEndpointsTests.Repeated_failures_lock_...` escrito, NÃO rodado (Testcontainers).
- [~] Resposta de login e recuperação sem enumeração. Login: feito e rodado em unitário (a chave do bloqueio é o identificador DIGITADO, então nome inexistente é bloqueado igual ao existente; 428/429 também não distinguem). Recuperação: NÃO feita, a rota "esqueci a senha" só nasce no ticket 09; o limitador (`ILimitadorDeLogin`) e a regra ficam prontos para ele reusar.
- [x] Login em local ou aparelho novo gera evento e alerta ao usuário e ao dono. Evidência: regra de aparelho/rede (`ImpressaoDeAcessoTests`, rodou); detecção, evento `seguranca.login-novo` no outbox, e-mail à pessoa e aos donos pelo despachante (`AlertasDeSegurancaIntegrationTests`, escrito, NÃO rodado). SQL da migração 0012 conferido à mão num PostgreSQL 16 local (RLS, índice único, poda em 50).
- [x] Reuso de refresh, bloqueio e troca de senha geram evento de segurança. Reuso já existia (ticket 04, `seguranca.refresh-reutilizado`). Novos: `seguranca.conta-bloqueada` (+ auditoria `autenticacao:bloquear`) e `seguranca.senha-trocada`, no outbox na mesma unidade de trabalho. Unitário rodou para o bloqueio (evento publicado uma vez, só para conta conhecida); a prova no outbox real está em `AlertasDeSegurancaIntegrationTests` (NÃO rodado).

## Decisões e limites (2026-11-05)

- Estado do bloqueio em MEMÓRIA do processo (`LimitadorDeLoginEmMemoria`): zera no restart e, com N instâncias, o limite efetivo é até N vezes maior. Porta `ILimitadorDeLogin` permite trocar por repositório compartilhado. Tabelas limitadas (100 mil chaves); cheia de entradas vivas, uma chave NOVA deixa de ser contada (falha aberta para ela).
- Bloqueio de conta por atacante é negação de serviço contra a vítima (quem digita o e-mail dela trava a conta por até 15 min). Aceito; a saída é a recuperação de acesso (ticket 09). Tentativa bloqueada não é auditada nem faz hashing (é o que fecha o DoS de login da dívida do ticket 04).
- IP: o endereço vem de `RemoteIpAddress`. Atrás do proxy do Railway é preciso ligar `Flow4Tech__Rede__ProxyConfiavel=true` (usa a última entrada de `X-Forwarded-For`, `ForwardLimit=1`); só é seguro se a API só for alcançada pelo proxy. Desligado, todos os clientes parecem um IP só.
- Bloqueio de IP não tem organização: vai ao log técnico (`login.ip-bloqueado`, IP só como resumo de 8 hex), sem evento no outbox e sem auditoria.
- CAPTCHA: `ICaptcha` sem provedor (`CaptchaNaoConfigurado`); enquanto `Configurado=false` o login nunca exige. Resposta quando exigido: 428 (corpo vazio); campo `captchaResposta` no corpo do login.
- E-mail: `IEnviadorDeEmail`, implementação de desenvolvimento só em memória (nenhum e-mail sai de verdade até a questão 4). O envio é efeito fora do banco: falha depois do envio e antes do commit reenvia (pelo menos uma vez).
- "Local" = prefixo de rede (IPv4 /24, IPv6 /64), sem geolocalização; "aparelho" = user agent sem dígitos. Primeiro login da conta só cria a base, não alerta. Guarda no máximo 50 por conta e tipo. Login após a própria troca de senha não é julgado nem aprendido.
- O despachante de eventos continua fora da DI e sem agendamento: os consumidores de alerta estão registrados como `IOutboxConsumer`, mas nada os executa em produção até o worker Quartz.
