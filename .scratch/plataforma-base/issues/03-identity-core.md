# 03: Identidade: contas, senhas e pré-autenticação sob RLS

**What to build:** Contas com e-mail e contas operacionais, Argon2id, checagem de senha vazada, credencial temporária com troca obrigatória, função SECURITY DEFINER mínima para o lookup de login, ADR-005. Spec 5.2.1, 5.2.2, 5.2.4, 5.2.7, 5.2.8.
**Blocked by:** 01, 02
**Touches:** `backend/src/Organizacao/**` (ou novo módulo `Identidade`), `backend/migrations/**`, `docs/adr/**`, `backend/modules.dependencies.json`
**Nature:** objective
**tdd-scope:** required
**Seams:** RLS, Architecture test
**Status:** done-local

- [x] Lookup de login não usa papel que ignora RLS; exceção listada em `RlsArchitectureIntegrationTests` (`PreAuthenticationIntegrationTests`, `IdentidadeSchemaIntegrationTests`: nenhum papel BYPASSRLS fora o superusuário do contêiner, conjunto exato das 3 funções SECURITY DEFINER com `search_path` fixo e sem EXECUTE para PUBLIC; `identidade_emails` em `Exempt` + `Exempt_tables_are_not_reachable_by_the_application_role`)
- [x] Login não revela se usuário, organização ou e-mail existem; tempo uniforme (`AutenticacaoIntegrationTests`: uma só `FalhaDeAutenticacao` para 9 caminhos; mesma contagem de trabalho de hash em 7 caminhos com um hasher que conta; piso de tempo `ResponseTimeFloor` aplicado a todo resultado. Uniformidade medida em tempo NÃO foi provada, só o piso mínimo)
- [x] Credencial temporária mostrada uma única vez; sessão inicial só troca a senha (`GestaoDeContasIntegrationTests`: devolvida no retorno e achada em nenhuma tabela; `Autenticacao` devolve `DeveTrocarSenha`; criar/desativar/definir dono recusados enquanto o ator deve trocar a senha; a sessão em si é do ticket 04)
- [x] Anti-autopromoção, sempre um dono e dono não rebaixado têm teste (`ProtecaoDeContaTests` puro + `GestaoDeContasIntegrationTests`, incluindo dois donos saindo ao mesmo tempo)
- [x] ADR-005 escrito e aceito (`docs/adr/005-identidade-propria.md`)
- [ ] ASSUMPTION: uma conta pertence a uma só organização (spec, questão 1); revisar se o dono disser o contrário

## Decisões e limites (implementação local)
- **Módulo novo `Identidade`** (Domain/Application/Infrastructure/Interface, `modules.dependencies.json` sem arestas): a regra de dependência permite, ela só precisa de Kernel e da infraestrutura Data/Audit, e `Organizacao` não deve conhecer senhas nem credenciais.
- Migração `0007_identidade.sql`: `contas`, `credenciais` (RLS exata, FKs compostas, CHECK de caixa baixa), FK composta de `usuario_unidades.usuario_id` para `contas`, tabela global `identidade_emails` (única exceção a `organizacao_id`, sem privilégio para a aplicação) e as três funções SECURITY DEFINER. A função de lookup trabalha SOB RLS (ajusta o contexto local para a organização da chave exata) e recusa rodar dentro de um contexto de organização; a única conexão sem contexto é `PreAuthDatabase` (Data), que só chama essas funções (gate de arquitetura).
- Sem rota HTTP e sem permissões novas: `GestaoDeContas`, `TrocaDeSenha` e `Autenticacao` são serviços de aplicação (registrados no host por `AddIdentidade`). `backend/permissions.catalog.txt` e `openapi.json` não mudaram; a autorização de QUEM chama os métodos administrativos (`usuario:*`) fica para as rotas (tickets 05/06).
- Regra de dono escolhida (estrita, conforme a spec 5.2.7): dono nunca é desativado nem rebaixado por outra pessoa, nem por outro dono; ninguém altera a própria permissão (inclui dono se rebaixando); sair exige outro dono. Transferência de titularidade fica como operação futura explícita.
- `CriarDonoAsync` (o fundador digita a própria senha) não verifica se a organização já tem dono: quem chama é o fluxo de onboarding.
- Auditoria nova (catálogo só cresce): `conta:criar`, `conta:desativar`, `conta:definir-dono`, `conta:trocar-senha`, `autenticacao:falhar`. Falha de organização/usuário inexistente NÃO é auditada (não há cadeia; gravar só para contas existentes seria um sinal); o ticket 08 conta essas tentativas.
- Identificador de organização no login operacional é o id (UUID) da organização; não existe apelido/slug.

## Riscos e pendências
- A função de lookup é uma porta deliberada que devolve o hash a quem conhece organização+usuário (ou e-mail). Mitigação: chave exata, Argon2id, bloqueio do ticket 08. Quem executa SQL arbitrário com o papel da aplicação consegue chamá-la depois de zerar o contexto (a recusa dentro de contexto é endurecimento, não prova).
- `identidade_registrar_email` permite a quem cria uma conta "reservar" um e-mail na plataforma: o convite do ticket 09 deve confirmar a posse do e-mail antes de criar a conta. Criar conta com e-mail já usado em outra organização revela que ele existe (a quem tem permissão de criar contas).
- Uma falha da gravação de auditoria de conta conhecida vira erro 500 (ficaria distinguível de conta desconhecida); aceito como indisponibilidade. Falhas repetidas de contas conhecidas escrevem na cadeia de auditoria sem limite até o ticket 08 (custo do lock e crescimento da cadeia).
- Tempo uniforme: só o piso mínimo é provado (limite inferior); se o trabalho real passar do piso (banco lento, Argon2 mais caro que 400 ms numa máquina pequena), a diferença volta a aparecer. Medir em staging e ajustar `Flow4Tech:Identidade:TempoMinimoDeLoginMs` e os parâmetros Argon2 (46 MiB por hash, até `ProcessorCount` hashes simultâneos).
- HIBP real nunca foi chamada (sem rede aqui): o contrato foi provado com `HttpMessageHandler` falso. O cliente HTTP padrão não reresolve DNS durante a vida do processo. O padrão do host é HIBP desligada (`Flow4Tech:Identidade:Hibp:Enabled`); só a lista local (pequena, 45 senhas) vale até alguém ligar.
- O login com várias organizações por e-mail e o login operacional por apelido de organização dependem de a questão 1 da spec ser respondida.
- Sessão do estado "deve trocar" só altera a senha: o ticket 04 precisa levar o estado na sessão; aqui só os serviços administrativos recusam o ator nesse estado.
- Os testes de Flow4Tech.Tests, Isolation e RLS foram ajustados só para criar a conta antes do vínculo (`TestDatabase.InsertContaAsync`) por causa da nova FK.
