# Novo sistema — Arquitetura e stack

Status: rascunho 1, **proposta para decisão**. Cada escolha aponta o requisito que a motiva (`01` a `03`). Decisões em aberto estão em A15.

## A1. Direcionadores (o que a arquitetura precisa entregar)

| # | Direcionador | Origem |
|---|---|---|
| 1 | Isolamento rígido entre organizações, imposto no banco | D3, S5 |
| 2 | Motor de permissões granular com condições, escopo por unidade e deny | D4, D10, S8 |
| 3 | Dados financeiros e de estoque imutáveis, com concorrência segura | S10 |
| 4 | Novas features sem regressão: módulos com fronteira, testes fortes | D6, RNF7 |
| 5 | Eventos de domínio estruturados alimentando notificações, KDS, fiscal, relatórios | F13, F6, F7 |
| 6 | Tempo real (alertas, fila de cozinha, comandas compartilhadas) | F3, F6, F1 |
| 7 | Integrações futuras trocáveis (TEF, Pix, NFC-e, impressoras, WhatsApp) por adapter | F4, F7, F13 |
| 8 | Crescer de 1 cliente a milhares sem reescrever; custo proporcional ao uso | D2, RNF1, RNF9 |
| 9 | Retenção por camadas, partições, relatórios sem competir com o salão | RNF2, ciclo de vida dos dados |
| 10 | Control Tower como domínio de identidade separado no mesmo deploy | D14 |
| 11 | Desenvolvimento assistido por IA com feedback rápido e seguro | D9 |

## A2. Forma geral: monólito modular orientado a eventos

**Decisão proposta (ADR-001):** um único sistema implantável, dividido em **módulos de domínio com fronteiras impostas por ferramenta**, comunicando-se por **interfaces públicas** e **eventos de domínio**. Não microsserviços.

Por quê:
- Com uma equipe pequena assistida por IA, microsserviços trocam complexidade de código por complexidade de operação (redes, versões, transações distribuídas) sem ganho hoje.
- Dinheiro e estoque exigem **transações fortes**; um banco único com transações locais evita sagas.
- Módulos bem isolados permitem **extrair um serviço depois** (fiscal, notificações, relatórios) se um dia for preciso.

```
┌────────────── Clientes ──────────────┐
│  App (React)        Control (React)  │
└───────┬───────────────────┬──────────┘
        │ HTTPS/SSE         │ HTTPS (host próprio, IP restrito)
┌───────▼───────────────────▼──────────┐
│            API  (um deploy)          │
│  Módulos: identidade · permissões ·  │
│  comandas · cardápio · estoque ·     │
│  pagamentos · caixa · financeiro ·   │
│  equipe · relatórios · notificações  │
│  · configurações · plataforma        │
│  Kernel: contexto de organização,    │
│  motor de decisão, eventos, auditoria│
└───────┬──────────────────────────────┘
        │ SQL (RLS)            ┌────────────────────┐
┌───────▼───────┐   outbox →   │  Worker(s)         │
│  PostgreSQL   │──────────────▶  notificações,     │
│ (+ réplica    │              │  agregados, fiscal │
│  de leitura)  │              │  arquivamento      │
└───────┬───────┘              └─────────┬──────────┘
        │                          Adapters externos
┌───────▼───────┐             (TEF, Pix, NFC-e, impressão,
│ Objetos (S3)  │              e-mail, WhatsApp)
│ anexos, frio  │
└───────────────┘
```

Unidades de implantação: **API**, **Worker** (mesmo código, outro processo), **App web**, **Control web**.

## A3. Módulos e regras de dependência

- Um módulo expõe uma **API pública pequena** (casos de uso + eventos publicados). Outros módulos só usam isso; acessar tabela ou classe interna de outro módulo é **proibido e falha no CI** (verificação automática de dependências).
- Estrutura interna de cada módulo, em camadas: **domínio** (regras puras, sem framework nem banco) → **aplicação** (casos de uso, transações, autorização) → **infraestrutura** (banco, adapters) → **interface** (rotas, DTOs).
- **Kernel compartilhado** (pequeno e estável): contexto de organização/unidade, tipos de dinheiro e identificadores, motor de decisão de permissões, barramento de eventos, auditoria, relógio.
- Mapa inicial: `identidade`, `permissoes`, `organizacao` (organizações, unidades, configurações em camadas), `cardapio`, `estoque`, `comandas`, `pagamentos`, `caixa`, `financeiro`, `equipe`, `cozinha`, `fiscal`, `notificacoes`, `relatorios`, `auditoria`, `plataforma` (Control Tower).
- **Dependências permitidas** declaradas em um arquivo (ex.: `comandas` → `cardapio`, `estoque`, `pagamentos`; ninguém → `comandas`, exceto por eventos). Ciclos proibidos.

## A4. Dados

**Banco: PostgreSQL** (ADR-002). Motivos: RLS nativo para isolamento imposto pelo banco (S5), transações fortes, particionamento (RNF1), JSONB para configuração flexível, ecossistema maduro, réplica de leitura.

Convenções:
- **Organização em toda tabela de negócio**, RLS ativo, política `organizacao_id = contexto atual`. A conexão da aplicação **não** é dona das tabelas e **não** ignora RLS; migrações usam outra credencial. Contexto definido por transação.
- **Identificadores UUID v7** (únicos, não adivinháveis, ordenáveis por tempo).
- **Dinheiro em inteiros (centavos)** + moeda; quantidades de estoque em decimal exato com unidade; nunca ponto flutuante.
- **Tempo em UTC**, fuso e dia operacional por unidade.
- **Registros imutáveis** (pagamentos, movimentos de estoque, lançamentos financeiros, movimentos de caixa, auditoria): sem `UPDATE`/`DELETE` concedidos ao usuário da aplicação; correção por estorno. Saldos materializados atualizados **na mesma transação**, com verificação de totais.
- **Auditoria** em tabela append-only, encadeada por hash, com cópia exportada para fora do banco principal.
- **Outbox transacional**: todo evento de domínio é gravado **na mesma transação** da mudança de estado; um despachante o entrega. Garante "aconteceu ⇔ evento existe", base de notificações, KDS, fiscal e agregados.
- **Particionamento por mês** em tabelas de alto volume (itens, movimentos, eventos, auditoria); **arquivamento** e **agregados** conforme o ciclo de vida dos dados (RNF).
- **Camada de leitura:** relatórios longos leem agregados e réplica de leitura; nunca competem com lançamento de itens.
- **Migrações** versionadas, reversíveis e compatíveis com deploy sem parada (expandir → migrar → contrair).
- **Acesso a dados SQL-first:** consultas explícitas e tipadas (ver A8), sem ORM que esconda SQL, para que RLS, transações e desempenho fiquem visíveis e testáveis.

## A5. Eventos, filas e tempo real

- **Barramento de eventos interno** com garantia via outbox; consumidores são idempotentes.
- **Fila de trabalhos em PostgreSQL** (ADR-003) no início: sem peça extra de infraestrutura, transacional com o outbox, suficiente para dezenas de eventos por segundo (RNF1: ~60/s). Interface de fila **abstraída** para trocar por SQS/Kafka se um dia o volume exigir.
- **Tempo real por SSE** (ADR-004): servidor → navegador, simples, atravessa proxies, reconecta sozinho com retomada (último id recebido). Cobre alertas, KDS e atualização de comanda compartilhada. WebSocket só se surgir necessidade bidirecional real.
- **Fan-out entre instâncias** da API via LISTEN/NOTIFY ou canal leve; canais **segregados por organização e permissão**.
- **Trabalho agendado** (arquivamento, agregados, reconciliações, vencimentos) por agendador no worker, com exclusão mútua.
- **Adapters** para tudo externo (pagamento/TEF, Pix, fiscal, impressão, e-mail, WhatsApp): interface estável, implementação trocável, **assíncronos, idempotentes, com retentativa e circuito de proteção**; hoje só o provedor "manual" de pagamento e a impressão de comprovante.

## A6. Identidade e autorização

**Identidade (ADR-005): implementação própria sobre primitivas consagradas**, não serviço terceirizado. Motivos: contas **sem e-mail** (usuário + senha), dois domínios de identidade (clientes e Control Tower), impersonação com atribuição dupla, revogação imediata e regras específicas (S6, S7, S13) não se encaixam bem em provedores prontos, e custo por usuário ativo cresceria com o negócio. Usar bibliotecas auditadas para hash (Argon2id), JWT/assinatura, OIDC (Google) e geração de tokens; **não** inventar criptografia.
- Duas "realms" lógicas: `app` e `control`, com tabelas, chaves de assinatura e audiência distintas.
- Refresh rotativo com detecção de reuso; sessões em tabela com versão para revogação imediata.

**Autorização (ADR-006): motor de decisão próprio, único ponto de decisão** (S8). Modelo `recurso:ação` + escopo (organização/unidade/objeto) + condições + deny. Avaliar **Cedar** (linguagem de políticas aberta, com garantias de análise) como base antes de construir do zero; decidir após um **spike** de uma semana contra três casos reais (desconto com limite, cortesia com teto diário, gerente só na unidade 2).
- Catálogo de permissões declarado **no código de cada módulo**, coletado automaticamente.
- Decisão exposta em um middleware/decorator único; verificação de que **nenhuma rota existe sem declaração de permissão** (teste de arquitetura).

## A7. API

- **REST com contrato OpenAPI** gerado a partir dos esquemas de validação (ADR-007). Motivo: integrações futuras e parceiros exigem contrato público estável; clientes tipados são gerados; contratos viram teste.
- Validação de entrada estrita por esquema em toda fronteira; tipos compartilhados entre front e back.
- **Paginação, filtros e ordenação padronizados**; limites rígidos; idempotência por cabeçalho nas operações de dinheiro.
- Erros padronizados (sem vazar detalhes), id de correlação em todas as respostas.
- Versionamento explícito (`/v1`), política de depreciação.
- **Rate limit por IP, usuário, dispositivo e organização.**

## A8. Linguagem e frameworks

**Decisão (ADR-008): back-end em C# (.NET LTS); front-end em React + TypeScript.** Dois idiomas, ligados por **contrato OpenAPI** como fonte única de verdade: a API gera o contrato, o contrato gera o cliente TypeScript tipado, e quebra de contrato falha o CI.

Por quê C#:
- Tipagem forte garantida em compilação e execução; `record`, pattern matching e nulos seguros modelam bem dinheiro, estados de comanda e permissões.
- ASP.NET Core traz segurança embutida (autorização por políticas, rate limiting, proteção de dados, CORS, CSRF).
- Cadeia de dependências (NuGet) mais conservadora que a do npm, relevante para o risco de supply chain.
- Ferramentas de teste e de verificação de arquitetura maduras; PostgreSQL e RLS bem atendidos pelo Npgsql.
- Folga de desempenho e portabilidade: contêiner Linux padrão, roda em qualquer nuvem.

| Camada | Escolha | Motivo |
|---|---|---|
| Linguagem/runtime | **C# e .NET LTS**, nulos habilitados, analisadores estritos | Segurança de tipos de domínio |
| API | **ASP.NET Core** (Minimal APIs ou controllers por módulo) | Autorização por políticas, rate limiting e OpenAPI nativos |
| Contrato | **OpenAPI** gerado do código; cliente TS gerado (ex.: openapi-typescript) | Fonte única de verdade, teste de contrato |
| Validação | FluentValidation (ou validação nativa) em toda fronteira | Entrada estrita |
| Acesso a dados | **Dapper** (consultas explícitas) e/ou **EF Core** com SQL visível; migrações SQL versionadas (ex.: DbUp, Flyway ou EF migrations revisadas) | RLS, transações e desempenho visíveis |
| Driver | **Npgsql**, contexto de organização definido por transação | RLS |
| Fila/worker | Fila sobre PostgreSQL (ex.: Wolverine, Hangfire ou Quartz) com outbox transacional, atrás de interface própria | Sem infra extra, trocável |
| Autenticação | Implementação própria sobre ASP.NET Core Identity/primitivas auditadas, Argon2id, OIDC para Google | Contas sem e-mail, duas realms, impersonação |
| Autorização | Motor próprio integrado às policies do ASP.NET; spike com Cedar | S8 |
| Tempo real | SSE (nativo no ASP.NET Core) | Alertas, KDS |
| Observabilidade | OpenTelemetry (.NET) + Sentry | RNF6 |
| Front | **React + Vite** (SPA), **TanStack Router/Query**, **Tailwind** + Radix/shadcn sobre design system próprio, React Hook Form + Zod | Ecossistema de UI, acessibilidade, PWA |
| Monorepo | Solução .NET para o back; pnpm workspaces para os fronts; pipeline único | Build incremental |

**Front mantido em React/TypeScript** (e não Blazor): o ecossistema de UI é o maior, o que favorece um front de alta qualidade visual; Blazor WASM tem primeiro carregamento pesado e Blazor Server depende de conexão permanente, ruim em rede instável de salão. A ausência de tipos compartilhados se resolve com cliente gerado do contrato.

**Alternativas consideradas:** TypeScript de ponta a ponta (velocidade e idioma único, mas cadeia npm mais arriscada e tipos só por validação), Python/FastAPI (familiar, tipagem opcional), Go (operação enxuta, domínio verboso), Kotlin (equivalente ao C#, JVM mais pesada).

**Portabilidade de nuvem (princípio):** o código **não depende de serviços proprietários de um provedor**. PostgreSQL puro sem extensões exclusivas, fila e arquivos atrás de interfaces (compatível com S3), configuração e segredos por variáveis de ambiente, entrega como contêiner. Trocar de nuvem altera só a infraestrutura como código.

## A9. Qualidade e testes (D6, RNF7)

- **Pirâmide:** testes unitários de domínio (rápidos, sem I/O) → integração por módulo **com PostgreSQL real** (Testcontainers, RLS ativo; xUnit) → contrato de API → ponta a ponta com **Playwright** nos fluxos críticos → carga com **k6**.
- **Testes de arquitetura automáticos** (ex.: NetArchTest/ArchUnitNET): fronteiras de módulo, rota sem permissão declarada, tabela sem RLS, consulta sem contexto de organização, uso de `float` para dinheiro.
- **Testes de isolamento entre organizações** gerados para cada rota (dois tenants) e **matriz de permissões** por perfil de fábrica.
- **Testes baseados em propriedades** em regras de dinheiro e estoque (divisão de conta, arredondamento, rateio, custo médio).
- **Testes de migração** (aplicar, reverter, aplicar em dados realistas).
- **CI com portões obrigatórios:** tipos, lint, testes, arquitetura, dependências, segredos, SAST; pré-visualização por ramo; deploy só de ramo principal verde.
- **Dados de teste sintéticos** versionados. Plano concreto (camadas, desenho do teste de isolamento, seeds S0 a S4, propriedades, E2E e carga) em `07-plano-de-testes.md`.

## A10. Infraestrutura e implantação

**Decisão (ADR-009): Railway na fase inicial**, com saída preparada para nuvem com região no Brasil (AWS ou GCP, São Paulo; cotar também Magalu Cloud) quando um dos gatilhos abaixo ocorrer.

**Ponto de partida: o que já existe e continua** (projeto Railway com ambientes `production` e `staging`, cada um com a API e um PostgreSQL em contêiner próprio, região Virgínia; front na Vercel). A mudança em relação ao sistema atual é pequena:

| Peça | Estado | Mudança |
|---|---|---|
| PostgreSQL | Railway (existente) | Nenhuma de infraestrutura; muda o esquema (RLS, partições, outbox) |
| API (C#) | Railway (existente) | Troca de linguagem; mesmo tipo de serviço |
| **Worker** | **Novo, opcional no início** | **Decisão:** no começo roda **dentro do próprio servidor da API** (mesmo processo, fila em PostgreSQL atrás de interface). Quando houver carga ou necessidade, vira **segundo serviço no Railway** (mesma imagem, comando de início diferente, sem domínio público), sem mudar o código. Processa notificações, arquivamento, agregados, relatórios pesados e futuras integrações. A separação precisa de teste: a documentação indica suporte, mas não confirma worker sem porta |
| App web (clientes) | Vercel (existente) | Reescrito; mesmo tipo de hospedagem |
| **Control Tower web** | **Mesmo projeto e mesmo endereço da Vercel, rota `/control`** | **Decisão:** começa como rota do app (um projeto, um deploy). Risco aceito no início: mesma origem do app dos clientes (ver S13). Condições obrigatórias: código do admin carregado **só ao entrar na rota**, CSP estrita, cookies com nome e caminho distintos, e toda a proteção real (identidade separada, restrição de IP, autorização) **na API**. **Evolução prevista:** extrair para `control.<domínio>` no mesmo projeto, trocando só domínio e cookies (ver A11.1) |
| **Armazenamento S3** | **Novo** | Anexos, XML de NF-e, dados frios. Bucket do Railway ou provedor externo barato (R2/B2), atrás de interface |
| Segredos | Variáveis de ambiente por ambiente (Railway/Vercel) | Nunca no repositório; rotação documentada |
| Observabilidade | OpenTelemetry + Sentry + logs exportados | Painel de operação na Control Tower |
| E-mail | Provedor transacional barato (SES/Resend) | Canais pagos depois |

**CDN:** não é preciso adicionar um. A **Vercel já serve os fronts por rede global de entrega**, inclusive próxima do Brasil. Cloudflare na frente da API fica **opcional**, para depois, se o abuso justificar (A10.1).

**Vercel e uso comercial:** conferir o plano. Pelo que conheço, o plano gratuito é para uso pessoal/não comercial e o plano Pro (cerca de US$ 20 por usuário/mês) cobre uso comercial; confirmar nos termos atuais.

**Região:** o Railway não tem região no Brasil (EUA Leste, EUA Oeste, Amsterdã, Singapura). Usar **EUA Leste (Virgínia)**, a mais próxima. Consequência: latência de rede maior para o salão.
- **Medição inicial (2026-10-21, da máquina do desenvolvedor, serviço atual na região `iad`/Virgínia):** tempo até o primeiro byte entre ~55 e ~155 ms (mediana ~95 ms) em 6 requisições a uma rota que responde 404, ou seja, mede só rede e borda do Railway, **sem trabalho de banco**. Dentro da meta de 300 ms, com folga. **Atualização (2026-10-31): a latência do salão até o Railway foi atestada como adequada pelo dono; item encerrado.** Reconfirmar apenas sob a carga real da Fase 2 (`07` T7). Registro original: uma primeira conexão levou ~6 s (provável resolução de DNS/TLS a frio); observar se se repete.
- ~~Ação obrigatória antes de congelar a meta de desempenho: medir a latência real.~~ **Cumprida** (ver atualização acima). Se, na carga real, o p95 de "lançar item" ficar acima de 300 ms por causa da rede, relaxar a meta nessa fase (ex.: 500 ms) ou antecipar a migração.
- Mitigações no desenho: ações otimistas na interface, requisições pequenas, SSE com retomada, sem chamadas em cascata.

**Banco e recuperação:** backups do Railway não equivalem a restauração a ponto no tempo. Para esta fase:
- **RPO de até 24 h passa a ser o compromisso formal** (risco aceito), com **backup lógico adicional** automático (diário e em horários de menor uso) enviado a armazenamento externo, criptografado.
- Avaliar arquivamento contínuo de WAL com ferramenta de backup (ex.: pgBackRest/WAL-G em serviço auxiliar) para chegar perto do RPO de 5 min **antes** do primeiro cliente pagante, se viável no Railway.
- Teste de restauração mensal permanece obrigatório.

**Gatilhos para migrar de nuvem** (qualquer um):
1. Primeiro cliente pagante que exija RPO/RTO do RNF3 ou região no Brasil.
2. Latência medida acima do aceitável após as mitigações.
3. Limites do Railway (rede privada, banco, backup, conformidade) barrando um requisito.
4. Custo por organização deixando de fazer sentido.

**Preparação para a saída (já na construção):** o princípio de portabilidade (A8) vale: Postgres puro sem extensões exclusivas, fila e arquivos atrás de interface, configuração por variáveis de ambiente, imagem de contêiner padrão, infraestrutura descrita em código (ver A10.2). Um **ensaio de migração** (subir o sistema em outra nuvem a partir de backup) deve ser feito antes de precisar.

### A10.1 Restrição por IP da Control Tower e proteção da API
- O **front** da Control Tower é estático e público (não carrega dados); quem precisa de proteção é a **API**.
- O Railway **não oferece lista de IPs permitidos de entrada**. Solução: a **API aceita as rotas da Control Tower somente de IPs permitidos**, validando o IP do cliente a partir do cabeçalho de encaminhamento do Railway, confiando só no proxy do Railway. A lista é configurável pelo super admin e a decisão negada é auditada.
- Proteção contra abuso: limite de taxa por IP, usuário e organização na própria API (S11); bloqueio progressivo no login (S6).
- **Opcional, depois:** CDN/WAF (ex.: Cloudflare) na frente da API para filtrar ataques volumétricos e aplicar regras de IP já na borda. Exigiria um segredo de origem (cabeçalho que só o Cloudflare envia) para impedir acesso direto ao Railway. Há relatos de problemas de domínio atrás do proxy do Cloudflare no Railway (duplo proxy, certificado, roteamento), então seria um spike próprio, incluindo SSE sem corte, **só se houver necessidade**.
- Cookies e domínios: front e API no mesmo domínio-raiz (ex.: `app.<domínio>` e `api.<domínio>`) para sessão segura (S7).

### A10.2 O que "infraestrutura descrita em código" significa aqui
Não é documentação: é um **arquivo executável e versionado que declara os recursos e cria/atualiza a infraestrutura**, como o CloudFormation faz na AWS. No Railway existem duas formas:
- **`railway.toml`/`railway.json`** por serviço: comando de build e de início, healthcheck, política de reinício, sobrescritas por ambiente.
- **Infraestrutura como código do projeto** (`.railway/railway.ts`, em TypeScript, com o comando `railway config plan/apply`): descreve serviços, bancos, volumes, buckets, domínios, variáveis e réplicas. Há também um provedor de Terraform **da comunidade** (não oficial).
(Detalhes e maturidade devem ser verificados na documentação atual antes de adotar.)

**O que fica portável e o que não** (importante para sair do Railway):
| Camada | Portável? |
|---|---|
| Aplicação (contêiner, Dockerfile, variáveis de ambiente, migrações SQL, scripts) | **Sim**, roda em qualquer nuvem |
| Arquivo de infraestrutura do Railway | **Não** (como o CloudFormation só serve na AWS); descreve recursos do Railway |
| Backups e dados | Sim, via exportação padrão do PostgreSQL e arquivos S3 |

**Estratégia:** (1) manter a **aplicação 100% portável**; (2) descrever o Railway em código para ter ambientes reproduzíveis e revisáveis (nada configurado só no painel); (3) **não** escrever agora o código de outra nuvem, porque seria desperdício; (4) manter um **roteiro de migração** (passos, ordem, verificação, reversão) e fazer um **ensaio antes de precisar**, subindo o sistema em outra nuvem a partir de um backup. O custo real da migração fica no passo "reescrever a infraestrutura na nuvem de destino" (Terraform ou o formato nativo dela), algo de dias, não de meses.

- **Ambientes:** desenvolvimento local (contêineres), homologação e produção, projetos separados; dados reais só em produção.
- **Deploy sem parada** (rolling), **reversão rápida**, feature flags, migrações compatíveis (expandir → migrar → contrair).
- **Custo inicial estimado:** US$ 25-45/mês no Railway (API + worker + Postgres pequenos) **mais o plano da Vercel** (a confirmar; Pro costuma ser cerca de US$ 20 por usuário/mês), crescendo com uso.

### A10.3 Control Tower: `/control` agora, `control.<domínio>` depois
Regras de projeto que mantêm a migração barata (cerca de um dia de trabalho, sem reescrever telas):
1. **Endereço-base configurável:** nenhuma tela escreve `/control/...` fixo; tudo usa uma configuração central.
2. **Cookies da Control Tower com nome e escopo próprios** desde o dia 1.
3. **Rotas de API da Control Tower sob prefixo próprio** (ex.: `/v1/control/...`), com restrição de IP e autorização aplicadas por esse prefixo.
4. **CSP e cabeçalhos de segurança configurados por rota**, para depois valerem por endereço.
5. **Código da Control Tower em pacote separado**, carregado sob demanda.

Migração prevista: adicionar o domínio novo ao mesmo projeto, exibir a Control Tower pelo endereço, trocar os cookies (administradores entram de novo uma vez), redirecionar `/control` por um período e atualizar a lista de endereços permitidos na API. Gatilho sugerido: **antes do primeiro cliente pagante** ou ao crescer o número de administradores.

## A11. Frontend: estrutura

- **Dois aplicativos** no monorepo: `app` (clientes) e `control` (plataforma), com **design system compartilhado** e **clientes de API gerados** a partir do contrato.
- Organização por **funcionalidade**, espelhando os módulos de back-end; cada funcionalidade com suas telas, hooks de dados e testes.
- **Permissões no front só mudam a interface** (esconder/desabilitar); a autoridade é do servidor (S1).
- Tempo real via SSE integrado ao cache de dados (invalidação por evento).
- **App do garçom (canal principal de lançamento, D16): começa como PWA instalável**, otimizado para velocidade (ver `02` F1). A API é o único contrato, então um **app nativo** (React Native/Expo para reaproveitar TypeScript, ou .NET MAUI) pode ser adicionado depois **sem mudar o back-end**. Gatilhos para ir ao nativo: impressão Bluetooth, notificações em segundo plano confiáveis, distribuição em loja, uso intenso de câmera/leitor, ou limite de desempenho percebido em aparelho do salão. Reconexão e fila visível de ações pendentes (sem offline completo, D7); reconexão e fila visível de ações pendentes (sem offline completo, D7).
- Desempenho: divisão de código por rota, orçamento de tamanho de pacote no CI, telas operacionais priorizadas.
- Acessibilidade e internacionalização desde o início (RNF8); textos externalizados.

## A12. Observabilidade e operação

- Logs estruturados, **id de correlação**, sem dado sensível; métricas por organização (custo e abuso); alertas acionáveis; rastreamento das operações críticas.
- **Painel de operação** na Control Tower: filas, jobs, falhas de integração, uso por organização.
- **Runbooks** versionados; **botões de emergência** (S16) implementados e testados.

## A13. Evolução prevista (sem reescrever)

| Quando | Mudança | Por que é barato |
|---|---|---|
| Mais volume de leitura | Réplica de leitura, mais agregados | Camada de leitura já separada |
| Mais volume de eventos | Fila dedicada (SQS/Kafka) | Fila e outbox atrás de interface |
| Fiscal, TEF, KDS | Novos módulos/adapters | Eventos e adapters previstos |
| Módulo pesado isolado | Extrair para serviço | Fronteiras de módulo já impostas |
| 2FA e reautenticação | Ligar política | Encaixes prontos (S6, S12) |
| Canais pagos | Novo adapter de notificação | Camada única de notificações |
| Migração de dados do legado | Importadores como módulo | Modelo próprio, sem acoplamento |

## A14. Registro de decisões (ADRs a escrever)

| ADR | Tema | Estado |
|---|---|---|
| 001 | Monólito modular orientado a eventos | Proposto |
| 002 | PostgreSQL com RLS | Proposto |
| 003 | Fila de trabalhos em PostgreSQL com outbox | Proposto |
| 004 | Tempo real por SSE | Proposto |
| 005 | Identidade própria, duas realms | Proposto |
| 006 | Motor de autorização próprio (spike com Cedar) | Proposto, spike |
| 007 | REST + OpenAPI gerado de esquemas | Proposto |
| 008 | C# (.NET) no back-end; React + TypeScript no front; contrato OpenAPI | **Decidido** |
| 009 | Railway na fase inicial, com gatilhos e preparação para migrar a nuvem com região no Brasil | **Decidido** |
| 010 | Multi-tenant: um banco compartilhado com RLS; banco dedicado como opção futura para clientes grandes | **Decidido** |

## A15. Decisões em aberto

1. ~~**Latência no salão**~~ **Fechada** (atestada em 2026-10-31); reconfirmar sob carga na Fase 2.
2. **Backup além do padrão do Railway:** definir a solução (backup lógico diário + WAL contínuo) e o momento de adoção (A10).
3. **Motor de autorização (ADR-006):** spike com Cedar antes de decidir entre usar ou construir.
4. **Biblioteca de fila e de acesso a dados:** escolher após spike curto (Wolverine/Hangfire/Quartz; Dapper/EF Core).
5. **Plano da Vercel** compatível com uso comercial e validação de IP na API repassado pelo Railway (A10.1). Cloudflare só se surgir necessidade.
6. **Spike do worker no Railway:** confirmar serviço sem domínio público, comando próprio e cron.
7. **Armazenamento S3:** escolher provedor (ex.: R2/B2).
