# Spec: Fundação técnica (Fase 0, itens 0.1, 0.2, 0.5 e 0.6)

Status: rascunho 1, para aprovação.
Origem: `06-roadmap` R3 (Fase 0); `04-arquitetura` A2 a A10; `03-seguranca` S5 e S8; `07-plano-de-testes` T1 e T2.

## 1. Objetivo

Deixar de pé a base técnica sobre a qual a Fase 1 (fatia vertical em `/salao`) é construída, e **decidir com evidência** os dois pontos de maior risco técnico: isolamento por RLS com acesso a dados, e o motor de autorização.

Ao final, um desenvolvedor (ou agente) consegue clonar o repositório, rodar tudo localmente, abrir um PR e ver o pipeline bloquear código que viole fronteira de módulo, isolamento entre organizações ou regra de permissão.

## 2. Escopo

| Item | Entrega | Origem |
|---|---|---|
| 0.1 | Repositório e estrutura: solução .NET modular, front React/TS, convenções, instruções para agentes | `06` 0.1 |
| 0.2 | CI/CD e ambientes: portões obrigatórios, `staging` e `production` no Railway, front na Vercel | `06` 0.2 |
| 0.5 | Spike do motor de autorização (Cedar contra três casos reais) | `06` 0.5 |
| 0.6 | Spike de dados e fila (Dapper/EF Core; Wolverine/Hangfire/Quartz; RLS com contexto por transação) | `06` 0.6 |
| 0.11 (parcial) | ADRs 002, 003, 006 e o registro das escolhas de 0.1/0.2 | `04` A14 |

## 3. Fora de escopo

- Qualquer módulo de negócio (comandas, cardápio, estoque, pagamentos). Só existe o suficiente para provar a infraestrutura.
- Identidade real (login, sessões, Argon2id): é a Fase 1 (1.3, 1.4).
- Spikes 0.4 (worker no Railway), 0.7 (backup) e 0.8 (plano da Vercel): tratados à parte com checklist e ADR.
- Protótipo de lançamento rápido (0.10) e direção visual (0.9).
- Infraestrutura em código do Railway (`.railway/railway.ts`): avaliar, não adotar nesta entrega (ver Questões abertas).

## 4. Decisões já tomadas (não reabrir)

- Monólito modular orientado a eventos (ADR-001, proposto).
- Back-end C# / .NET LTS; front React + TypeScript + Vite; contrato OpenAPI como fonte única (ADR-008, decidido).
- PostgreSQL com RLS; um banco compartilhado (ADR-002 proposto, ADR-010 decidido).
- Railway em `production` e `staging`, front na Vercel (ADR-009, decidido).
- Dinheiro em inteiros (centavos), UUID v7, tempo em UTC, SQL explícito sem ORM que esconda SQL.
- Aplicação portável: Postgres puro sem extensões exclusivas, fila e arquivos atrás de interface, configuração por variáveis de ambiente, contêiner padrão.

## 5. Requisitos

### 5.1 Repositório e estrutura (0.1)

1. Monorepo com: solução .NET (back-end modular), `pnpm workspaces` para `app` e `control` (front) e um pacote de design system compartilhado (esqueleto vazio).
2. Um projeto/pasta por módulo do mapa inicial de `04` A3. Nesta entrega, só ficam **criados e vazios** os módulos usados pelos spikes (`organizacao`, `permissoes`) mais o **Kernel** (contexto de organização, tipos de dinheiro e identificadores, relógio). Os demais entram nas fases correspondentes.
3. Estrutura interna de cada módulo em camadas: domínio, aplicação, infraestrutura, interface.
4. Dependências permitidas entre módulos declaradas em **um arquivo**; ciclos e acesso a internos de outro módulo falham no CI.
5. Nulos habilitados e analisadores estritos no .NET; TypeScript em modo estrito.
6. Novo `CLAUDE.md`/`AGENTS.md` na raiz descrevendo: estrutura, comandos de build/teste, regras de fronteira, convenções de dinheiro/tempo/IDs, e como escrever uma rota "do jeito seguro".
7. Desenvolvimento local por contêineres: um comando sobe Postgres e a API; outro roda os testes.
8. Cliente TypeScript **gerado** do contrato OpenAPI; quebra de contrato falha o CI.
9. Pasta `docs/adr/` com modelo de ADR; o conteúdo atual de `.scratch/novo-sistema/` permanece como planejamento.

### 5.2 CI/CD e ambientes (0.2)

1. Pipeline único com **portões obrigatórios**: build, tipos, lint, testes unitários, testes de integração com PostgreSQL real, testes de arquitetura, varredura de segredos, verificação de dependências vulneráveis, contrato OpenAPI.
2. Merge só com pipeline verde; deploy só do ramo principal verde.
3. Ambientes `staging` e `production` no Railway (API + PostgreSQL por ambiente) e front na Vercel, com variáveis de ambiente por ambiente; nenhum segredo no repositório.
4. Endpoint de saúde (liveness e readiness, esta última checando o banco) usado pelo healthcheck do Railway.
5. Migrações de banco aplicadas no deploy, versionadas, no padrão expandir → migrar → contrair; credencial de migração **separada** da credencial da aplicação.
6. Pré-visualização por ramo do front (Vercel).
7. Reversão do deploy documentada e testada uma vez em `staging`.

### 5.3 Spike de dados e fila (0.6)

Prova de conceito, descartável no código mas **permanente nos testes e no ADR**.

1. Esquema mínimo: `organizacoes`, `unidades` e uma tabela de negócio de exemplo com `organizacao_id`, RLS ativo e política `organizacao_id = contexto atual`.
2. Duas credenciais de banco: dona/migração e aplicação. A aplicação **não** é dona das tabelas e **não** ignora RLS.
3. Contexto da organização definido **por transação** (Npgsql) e restabelecido em trabalhos em segundo plano.
4. Comparar **Dapper** e **EF Core** em: clareza do SQL, tipagem, aplicação do contexto por transação, desempenho e facilidade de teste. Escolher um.
5. Comparar **Wolverine**, **Hangfire** e **Quartz** para fila/worker com **outbox transacional** (evento gravado na mesma transação da mudança de estado) atrás de interface própria. Escolher um.
6. Provar o outbox: mudança de estado e evento atômicos; despachante entrega pelo menos uma vez; consumidor idempotente.
7. **Primeiro teste de isolamento entre organizações** (modelo de `07` T2): dois tenants; leitura e escrita cruzadas falham; consulta sem contexto retorna zero linhas ou erro; passa com a conexão real da aplicação, não com a dona.
8. Teste de arquitetura: tabela de negócio sem RLS ou sem `organizacao_id` falha o CI.
9. Medir e registrar: latência de uma transação típica (definir contexto + inserir + gravar outbox) em `staging`.

### 5.4 Spike do motor de autorização (0.5)

1. Avaliar **Cedar** (ou equivalente) como base do motor, contra os três casos reais:
   - **Desconto com limite:** garçom pode dar desconto até X% por perfil; acima disso, negado.
   - **Cortesia com teto diário:** limite acumulado por dia, avaliado na mesma transação para evitar corrida (`03` S8).
   - **Gerente só na unidade 2:** mesma permissão, escopo por unidade.
2. Cobrir também: deny explícito vence allow; negado por padrão; permissão desconhecida nega.
3. Critérios de comparação: expressividade para limites acumulados, integração com ASP.NET (policies), desempenho de decisão, testabilidade (matriz perfil × permissão), risco de dependência (maturidade do binding .NET).
4. Entregar uma interface única de decisão (`Decidir(quem, recurso:ação, onde, contexto)`) independente da escolha, para que usar ou construir não mude os chamadores.
5. Teste de arquitetura: rota sem declaração de permissão falha o CI.
6. Decisão registrada: **usar Cedar**, **construir** ou **híbrido**, com justificativa.

## 6. Critérios de aceite

- [ ] `git clone` + um comando sobe ambiente local completo; outro roda todos os testes.
- [ ] PR que importa classe interna de outro módulo falha no CI.
- [ ] PR que cria tabela de negócio sem RLS falha no CI.
- [ ] PR que cria rota sem permissão declarada falha no CI.
- [ ] Teste de isolamento com dois tenants passa usando a credencial da aplicação (sem bypass).
- [ ] Outbox: falha forçada entre a mudança de estado e o despacho não perde nem duplica efeito observável.
- [ ] Os três casos de permissão têm teste automatizado passando no motor escolhido.
- [ ] `staging` no ar com deploy automático do ramo principal; `production` provisionado.
- [ ] Cliente TypeScript gerado e verificado no CI.
- [ ] ADR-002, 003 e 006 escritos e aprovados; escolha de acesso a dados (Dapper/EF) e de fila registrada.
- [ ] Reversão de deploy exercitada uma vez em `staging`.

## 7. Ordem sugerida

1. Esqueleto do repo e ambiente local (5.1) → destrava o resto.
2. Pipeline com portões mínimos (5.2.1 e 5.2.2) e `staging` (5.2.3 a 5.2.5).
3. Spike de dados e RLS (5.3), em paralelo com o spike de autorização (5.4) depois que o esqueleto existir.
4. Fechar os portões que dependem dos spikes (testes de arquitetura sobre RLS e permissão), ADRs e reversão de deploy.

## 8. Riscos

| Risco | Mitigação |
|---|---|
| Binding .NET do Cedar imaturo ou limite acumulado inexpressível | Spike com prazo curto; interface única de decisão permite construir o motor sem mudar chamadores |
| RLS com pool de conexões vazar contexto entre requisições | Contexto por transação (`SET LOCAL`), teste que reutiliza a mesma conexão entre dois tenants |
| Spike virar produto sem revisão | Código do spike descartável; ficam os testes, o ADR e a interface |
| Pipeline lento desencoraja uso | Orçamento de tempo do pipeline registrado; testes de integração paralelizados |
| Dependência do Railway sem teste de worker (`06` 0.4) | Fora desta entrega; worker nasce dentro da API atrás de interface |

## 9. Questões abertas

1. Infraestrutura do Railway em código (`railway.toml` apenas, ou também `.railway/railway.ts`)? Verificar maturidade antes de adotar.
2. Nome do repositório/solução e do namespace raiz.
3. Qual identidade visual (v6 ou v7) alimenta o esqueleto do design system? Não bloqueia esta spec.
4. Ferramenta de migrações SQL: DbUp, Flyway ou EF migrations revisadas (depende do resultado de 5.3.4).
5. Provedor de varredura de segredos e de dependências no CI.
