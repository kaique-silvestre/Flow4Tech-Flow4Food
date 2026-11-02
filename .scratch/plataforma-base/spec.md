# Spec: Plataforma base (Fase 1A, itens 1.1, 1.3, 1.4, 1.6 ligado ao HTTP, 1.7, 1.10 e 1.11)

Status: rascunho 1, para aprovação.
Origem: `06-roadmap` R4; `03-seguranca` S5 a S8, S12 e S14; `02-features` F11 e F14; `04-arquitetura` A6 e A7; ADR-002, 003, 006.
Fatiamento: a Fase 1 foi dividida em duas specs, aprovado pelo dono. Esta é a **1A**. A **1B** (fatia `/salao`, notificações e SSE, Control Tower mínima, observabilidade) só começa depois da 1A e depois da spec do modelo de domínio.

## 1. Objetivo

Fazer com que **toda requisição HTTP saiba quem é o usuário, em qual organização e unidade ele está, e o que ele pode fazer**, e que cada ato relevante fique registrado de forma verificável.

Hoje a Fase 0 entrega as peças soltas: RLS com contexto por transação, outbox, motor `IAuthorizationEngine.DecidirAsync` e rotas que declaram permissão. Falta ligá-las ao HTTP, porque não existe identidade nem sessão (`OrganizationContext` ainda é um placeholder).

Ao final, uma rota de negócio pode ser escrita seguindo o `CLAUDE.md` ("Writing a route the secure way") de ponta a ponta: autenticada, com organização e unidade vindas da sessão, decidida pelo motor, auditada e coberta por teste de dois tenants.

## 2. Escopo

| Item | Entrega | Origem |
|---|---|---|
| 1.1 | Organização e unidades; contexto de organização e unidade resolvido em toda requisição a partir da sessão | `06` 1.1, `03` S5 |
| 1.3 | Identidade: e-mail/senha, conta operacional (usuário + senha com troca obrigatória), Google, convite, recuperação de acesso | `06` 1.3, `03` S6, `02` F11 |
| 1.4 | Sessões e tokens: acesso curto, refresh rotativo com detecção de reuso, revogação imediata, lista de sessões | `06` 1.4, `03` S7 |
| 1.6 (HTTP) | Motor de permissões ligado ao HTTP: `PermissionHandler`, perfis, escopo por unidade, anti-autopromoção, simulador, matriz testada | `06` 1.6, `03` S8, ADR-006 |
| 1.7 | Auditoria imutável encadeada e log técnico com correlação | `06` 1.7, `03` S14 |
| 1.10 | Configurações em camadas (organização > unidade), com histórico | `06` 1.10, `02` F14 |
| 1.11 | Proteção de login e alertas de segurança | `06` 1.11, `03` S6 e S12 |

## 3. Fora de escopo

- **1B**: fatia `/salao`, notificações e SSE (1.9), Control Tower mínima (1.13), observabilidade (1.14).
- 1.5 (PIN e dispositivos): removido do roadmap. Sem PIN, sem vínculo a dispositivo.
- 2FA e reautenticação em ação de alto risco: o **encaixe** existe (campo de política, desligado), nada é construído (`03` S12).
- Control Tower e domínio de identidade `control`: o desenho da separação entra aqui (audiência de token distinta), mas o login do super admin é da 1B.
- Impersonação, planos, cobrança.
- Modelo de domínio (comanda, item, pagamento, caixa, mesa, cliente): spec própria antes da 1B.
- Persistência das regras de dia operacional por unidade (roadmap 2.10b): o resolvedor atual continua; as configurações em camadas desta spec **preparam** o lugar, sem trocar o resolvedor.
- Outras entregas do outbox já decididas mas adiadas (alerta e reprocesso de eventos estacionados, poda de 30 dias): continuam para antes do primeiro worker em produção, salvo a descoberta de organizações pendentes (ver 5.8).

## 4. Decisões já tomadas (não reabrir)

- Identidade **própria** sobre primitivas consagradas, não serviço terceirizado (`04` A6, ADR-005). Argon2id, assinatura de token e OIDC via bibliotecas auditadas; nenhuma criptografia inventada.
- Duas realms lógicas (`app` e `control`) com tabelas, chaves e audiência distintas; um token não vale no outro (`03` S7, S13).
- Um único ponto de decisão: `IAuthorizationEngine.DecidirAsync` (ADR-006). Nenhum código ramifica por nome de perfil.
- Reserva do teto e operação na mesma transação (`IUnitOfWork`). Dia do teto = dia operacional da unidade. Limites só em BRL.
- Só usuário/e-mail e senha para o salão, sem PIN. A sessão do salão dura até o fim do turno (relógio configurável); o token de acesso é curto e invisível.
- Sem 2FA no lançamento (risco aceito, mitigações em 5.5).
- Organização e unidade vêm **sempre da sessão**, nunca de parâmetro do cliente (`03` S5).
- Login com **Google** fica **dentro** da Fase 1 (decidido pelo dono nesta rodada, contrariando a simplificação do handoff).

## 5. Requisitos

### 5.1 Organização, unidades e contexto da requisição (1.1)

1. Cadastro de organizações e unidades como módulo `Organizacao` (tabelas já existem em `0001`); a unidade ativa de uma sessão é uma unidade a que o usuário tem vínculo.
2. Um **middleware único** resolve `OrganizationContext(OrganizationId, UnitId)` a partir da sessão autenticada. Rota autenticada sem contexto resolvido responde 401; usuário sem vínculo com a unidade pedida responde 403.
3. Nenhuma rota lê organização ou unidade de rota, corpo, query ou cabeçalho do cliente. Teste de arquitetura falha se um DTO de entrada tiver campo `organizacaoId`.
4. Trocar de unidade é uma operação explícita da sessão (nova unidade ativa), autorizada e auditada. O usuário só vê as unidades em que tem vínculo.
5. O contexto alimenta `TenantDatabase.BeginAsync`/`IUnitOfWork` sem o código de negócio montá-lo.
6. **Consultas anteriores ao contexto** (login, convite, recuperação, refresh) não têm organização ainda: ver 5.2.4 para como isso convive com o RLS.

### 5.2 Identidade (1.3)

1. **Dois tipos de conta** (F11):
   - **Conta com e-mail** (dono, gerente, administrativo): convite por link de uso único; a pessoa define a própria senha; pode entrar com Google.
   - **Conta operacional** (garçom, caixa): usuário + senha, sem e-mail. O nome de usuário é único **dentro da organização**; o login informa organização + usuário. Credencial temporária mostrada **uma única vez**, troca obrigatória no primeiro acesso. Ninguém define a senha de outra pessoa.
2. **Senhas:** Argon2id; comprimento mínimo, sem exigência de símbolos; checagem contra senhas vazadas (k-anonimato, sem enviar a senha); sem limite máximo irrisório; senha nunca em log, auditoria ou resposta.
3. **Google (OIDC):** só e-mail verificado; vincular a conta existente exige confirmação autenticada; **nunca cria conta automaticamente** em organização existente. Conta Google sem vínculo recebe resposta neutra.
4. **Tabelas de identidade e RLS.** O login precisa achar o usuário **antes** de existir contexto de organização. Decisão proposta: o lookup de pré-autenticação usa uma função `SECURITY DEFINER` restrita (por organização+usuário ou por e-mail), que devolve apenas o necessário para validar a credencial, sem papel que ignore o RLS e sem leitura geral de tabela. Depois do login, tudo o mais roda sob RLS. A tabela de e-mails globais é a única exceção prevista à regra de `organizacao_id`, listada em `RlsArchitectureIntegrationTests`. *Ver questão aberta 1.*
5. **Recuperação de acesso:** conta com e-mail usa "esqueci a senha" (link de uso único, expiração curta, invalida todas as sessões, notifica o usuário). Conta operacional: quem tem `usuario:redefinir_acesso` gera nova credencial temporária.
6. **Convites:** token aleatório de uso único, com expiração, vinculado ao e-mail e ao perfil. Só guarda o hash do token.
7. **Regras de proteção:** dono não é desativado nem rebaixado por outro; ninguém altera as próprias permissões; sempre existe pelo menos um dono; desativar usuário não apaga histórico e derruba as sessões.
8. Respostas de login **não revelam** se o usuário, a organização ou o e-mail existem; tempo de resposta uniforme.
9. Envio de e-mail (convite, recuperação, alerta) atrás de uma interface, com implementação de desenvolvimento que grava em memória/log. Provedor real: questão aberta 4.
10. Permissões do catálogo a declarar (F11): `usuario:ver`, `usuario:convidar`, `usuario:desativar`, `usuario:redefinir_acesso`, `perfil:ver`, `perfil:gerenciar`, `permissao:atribuir`, `sessao:ver_todas`, `sessao:revogar`, `organizacao:configurar`, `unidade:gerenciar`. Todas as marcadas [A] são auditadas (5.6).

### 5.3 Sessões e tokens (1.4)

1. **Token de acesso** curto (minutos), assinado, com audiência (`app`), organização, usuário, id de sessão e **versão de sessão**. Sem o token em `localStorage`.
2. **Refresh rotativo** com detecção de reuso: reapresentar um refresh já usado derruba a cadeia inteira da sessão e gera evento de segurança.
3. Cookies `HttpOnly`, `Secure`, `SameSite`; proteção contra CSRF onde houver cookie.
4. **Dois relógios:** o do token de acesso é sempre curto e invisível; o da sessão tem limite por inatividade e limite absoluto, **configuráveis por perfil e por organização** (5.7), com padrão generoso (salão: até o fim do turno; dono e gerente: dias). Política mais restrita para administradores existe, desligada por padrão.
5. **Revogação imediata:** desativar usuário, trocar senha, mudar permissão ou remover de unidade invalida as sessões afetadas. Mecanismo: versão de sessão consultada no servidor a cada requisição (ou lista de revogação), decidido em ADR (ADR-005).
6. Sessões listáveis e encerráveis pelo próprio usuário (`sessao` dele) e por quem tem `sessao:ver_todas`/`sessao:revogar`.
7. Registro e alerta de login novo (5.5.5).
8. Chaves de assinatura por realm, rotacionáveis, vindas de variável de ambiente; nunca no repositório.

### 5.4 Permissões ligadas ao HTTP (1.6)

1. `PermissionHandler : AuthorizationHandler<PermissionRequirement, OperationFacts>` converte as claims da sessão em `Subject` e chama `DecidirAsync` (modelo em `backend/spikes/authz`). Rota sem sessão: 401. Negado: 403 sem revelar o motivo; o `Decision.Reason` vai ao log técnico.
2. `RequirePermission` deixa de ser só metadado: passa a **exigir** autenticação e a ser avaliado pelo handler. Quando o motor precisa de valores da operação (limite, teto), a decisão é pedida no handler de aplicação, depois de carregar a operação real (regra já existente do `CLAUDE.md`).
3. **Perfis:** perfis de fábrica (não editáveis, só duplicáveis), perfis personalizados por organização, modelos de permissão (com escolha explícita "acompanhar o modelo" ou "copiar") e overrides individuais. Escopo por organização inteira ou unidades específicas. Condições e limites por permissão (valor, %); deny explícito vence allow.
4. As regras e perfis ficam **persistidos por organização** (hoje são `PolicySet` em código). O catálogo continua sendo gerado do código; permissão desconhecida nega.
5. **Anti-autopromoção:** ninguém altera as próprias permissões; só concede o que tem (sem escalada transitiva).
6. **Simulador** "o que este usuário pode fazer?" usa o mesmo motor (`DecidirAsync`), nunca uma cópia da lógica, e exige `perfil:ver`/`permissao:atribuir`.
7. **Matriz testada:** o teste de contrato do motor (perfil x catálogo) cresce para cobrir as permissões novas desta spec e passa a rodar também sobre as regras lidas do banco.
8. Toda decisão negada em recurso sensível é registrada (5.6) e alimenta o alerta de acesso negado em série (5.5).
9. Mudar permissão, perfil ou vínculo invalida as sessões afetadas (5.3.5).

### 5.5 Proteção de login e alertas (1.11)

1. Bloqueio **progressivo** por conta **e** por IP, com atraso uniforme; limites distintos para conta operacional e conta com e-mail.
2. Detecção simples de *credential stuffing* (muitas contas distintas por IP, muitos IPs para a mesma conta) com bloqueio temporário; CAPTCHA adaptativo fica como encaixe (interface) sem fornecedor.
3. Resposta de login e de recuperação sem enumeração de usuário.
4. Conta operacional **não acessa** funções administrativas, financeiras ou de exportação por padrão (perfil de fábrica), conferido pela matriz (5.4.7).
5. **Alerta de login novo** (dispositivo ou local ainda não visto para aquela conta), para o próprio usuário e para o dono, pelos canais disponíveis (e-mail; in-app chega na 1B). Eventos de segurança viram eventos de domínio no outbox.
6. Eventos de segurança registrados: bloqueio, reuso de refresh, troca de senha, convite usado, vínculo Google.

### 5.6 Auditoria (1.7)

1. **Log de auditoria append-only, encadeado por hash** (cada registro guarda o hash do anterior da mesma organização). `UPDATE` e `DELETE` negados à credencial da aplicação por privilégio (não só por convenção) e testados.
2. Cobre: autenticação (sucesso, falha, bloqueio), mudanças de permissão e perfil, convites e redefinições, ações [A] do catálogo, acessos negados em recurso sensível, mudança de configuração e troca de unidade.
3. Cada registro tem: quem (usuário e sessão), o quê (`recurso:ação`), onde (organização, unidade, objeto), antes/depois quando houver, resultado, **id de correlação** e instante em UTC (`IClock`). **Nunca** segredos, senhas, tokens ou hash de senha.
4. A escrita da auditoria acontece **na mesma transação** da ação (`IUnitOfWork`): sem ação sem registro e sem registro de ação que falhou, exceto os registros de falha deliberada (login negado), gravados à parte.
5. **Verificação de integridade** da cadeia: função e teste que detectam registro alterado, removido ou reordenado; execução periódica por trabalho em segundo plano. Falha de integridade gera evento de segurança. *A cópia fora do banco principal exigida em `03` S12 e S14 fica registrada como dívida desta spec (questão aberta 3).*
6. Log técnico estruturado separado da auditoria, ambos com id de correlação (`traceparent`) e sem segredos; teste que procura segredos conhecidos no log.
7. O catálogo de eventos de auditoria é versionado; nome de evento nunca é renomeado depois de publicado.

### 5.7 Configurações em camadas (1.10)

1. Valores resolvidos em camadas: **padrão do sistema > organização > unidade**. A unidade herda da organização e pode sobrescrever, salvo opção **travada** pela camada acima.
2. Cada opção é declarada no código (chave, tipo, padrão, camadas em que vale), como o catálogo de permissões, e coletada automaticamente.
3. **Histórico:** quem mudou, quando, valor anterior; restaurar versão anterior. Alteração é auditada (5.6) e exige `organizacao:configurar`.
4. **Aplicação segura:** mudança com efeito operacional vale a partir de um instante definido, nunca retroativa (F14.3). Nesta entrega o mecanismo existe e é testado; as opções operacionais reais entram com os módulos que as usam.
5. Opções iniciais desta spec: tempos da sessão (5.3.4), política de bloqueio de login (5.5), e o fuso e a virada do dia operacional (lugar onde o 2.10b vai persistir; o resolvedor atual continua lendo o padrão).
6. Valor por organização e por unidade passa por RLS como qualquer tabela de negócio.

### 5.8 Despachante de eventos e organizações pendentes (item aberto do ADR-003)

Os eventos de segurança e de auditoria dependem do outbox. O dispatcher precisa descobrir quais organizações têm eventos pendentes, já que o RLS esconde tudo sem contexto. Decisão proposta: tabela-registro `organizacoes_com_eventos_pendentes` (organização e instante), mantida na mesma transação em que o evento é gravado, lida pelo dispatcher por função restrita, **sem papel que ignora o RLS** (já previsto no handoff). *Questão aberta 2 confirma o desenho.*

## 6. Critérios de aceite

- [ ] Requisição sem sessão a rota protegida responde 401; com sessão sem a permissão responde 403 (sem revelar o motivo); rota com permissão e escopo corretos responde 2xx. Cobertos por teste para o caso permitido e o negado.
- [ ] Nenhuma rota recebe organização ou unidade do cliente; o teste de arquitetura falha ao introduzir um campo `organizacaoId` em DTO de entrada.
- [ ] Dois tenants de teste: usuário de A não lê nem altera dado, sessão, convite, configuração ou auditoria de B (credencial da aplicação, sem bypass), inclusive nas tabelas de identidade.
- [ ] Reapresentar um refresh já usado derruba a cadeia da sessão; desativar usuário, trocar senha e mudar permissão invalidam as sessões na próxima requisição.
- [ ] Token do realm `control` é recusado no realm `app` e vice-versa.
- [ ] Login não revela se o usuário existe; bloqueio progressivo por conta e por IP demonstrado em teste.
- [ ] Credencial temporária aparece uma única vez, e a primeira sessão só pode trocar a senha até a troca ser feita.
- [ ] Login com Google só aceita e-mail verificado e nunca cria conta sozinho.
- [ ] Anti-autopromoção, "sempre um dono" e "dono não é rebaixado" têm teste.
- [ ] Matriz perfil x permissão verde com as permissões desta spec; o simulador devolve o mesmo resultado do motor.
- [ ] Auditoria: `UPDATE`/`DELETE` negados à credencial da aplicação; adulteração de um registro é detectada pela verificação de cadeia; ação [A] sem registro falha o teste.
- [ ] Registro e correlação: a mesma id de correlação aparece na requisição, no log técnico e na auditoria; teste de varredura não acha senha, token nem hash de senha.
- [ ] Configuração: herança, trava, histórico e restauração testados; mudança com efeito operacional não retroage.
- [ ] Dispatcher entrega eventos de segurança de qualquer organização pendente sem ler dados de outra e sem papel que ignore RLS.
- [ ] Pipeline continua verde (`gates`), `openapi.json` expõe a permissão exigida de cada rota (`x-permission`) e o cliente TypeScript é regenerado.
- [ ] ADRs novos escritos e aceitos: identidade e sessões (ADR-005), política de revogação e, se mudar, o desenho de pré-autenticação sob RLS.
- [ ] `CLAUDE.md` atualizado com a rota autenticada de ponta a ponta.

## 7. Ordem sugerida

1. Contexto da requisição e unidades (5.1) com identidade mínima de teste. Destrava o resto.
2. Identidade e pré-autenticação sob RLS (5.2.1 a 5.2.4), depois sessões e tokens (5.3).
3. Permissões ligadas ao HTTP (5.4) e `x-permission` no contrato.
4. Auditoria (5.6) e organizações pendentes do dispatcher (5.8).
5. Proteção de login e alertas (5.5), Google, recuperação e convites.
6. Configurações em camadas (5.7).

Cada passo vira tickets com testes primeiro, revisor independente e relatório em português, no mesmo processo da Fase 0.

## 8. Riscos

| Risco | Mitigação |
|---|---|
| Pré-autenticação (login) abre buraco no RLS | Função `SECURITY DEFINER` mínima, sem papel que ignora RLS; teste de isolamento nas tabelas de identidade; exceção listada e testada |
| Rota sem contexto cai em consulta global | Todo acesso a dados já exige `TenantDatabase`; teste de arquitetura contra `organizacaoId` vindo do cliente |
| Revogação "imediata" custar uma consulta por requisição | Versão de sessão em cache curto invalidado por evento; medir a latência em `staging` |
| Escopo grande para uma spec | Passos independentes em 7; qualquer um pode ser cortado para a 1B sem refazer os demais |
| Auditoria sem cópia externa não cumpre S14 | Registrada como dívida explícita; cadeia verificável já protege contra adulteração silenciosa |
| Google OIDC vincular conta errada | Vínculo só com confirmação autenticada e e-mail verificado; teste do fluxo |
| Pipeline nunca rodou no GitHub | Dívida herdada da Fase 0; os tickets desta spec rodam o primeiro pipeline real |

## 9. Questões abertas

1. **Usuário em mais de uma organização.** Um mesmo e-mail pode pertencer a duas organizações (dono de dois restaurantes), ou cada conta é de uma só? Define a tabela de e-mails globais (5.2.4) e o fluxo de escolha no login.
2. **Descoberta de organizações pendentes.** Confirmar a tabela-registro (5.8) como desenho do ADR-003.
3. **Cópia da auditoria fora do banco principal.** Adiar para antes do primeiro cliente pago (dívida desta spec)?
4. **Provedor de e-mail e de CAPTCHA.** Qual usar em `staging`? Hoje só existe a interface.
5. **Tempos padrão da sessão.** Inatividade e duração máxima por perfil (sugestão: salão até o fim do turno; dono e gerente, dias). Confirmar números.
6. **Conta Google e conta operacional.** A conta operacional (sem e-mail) nunca usa Google; confirmar.
7. **Identidade visual (v6 ou v7).** Não bloqueia esta spec; entra na 1B.
