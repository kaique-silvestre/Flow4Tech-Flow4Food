# Hospedagem do front e ambientes do sistema novo (notas para discutir)

Aberta: 2026-11-07. Ticket: F4-0078. Estado: **em discussao**; nada aqui foi decidido. A decisao em vigor continua sendo o ADR-009 (API e PostgreSQL no Railway, front na Vercel).

## Ja decidido e em andamento
- Projeto novo no Railway, `flow4tech-v2` (id `a8497d34-eded-4659-a4ac-92512b5b4aab`), ambientes `production` e `staging`. O projeto antigo `flow4food` e o sistema antigo e nao se mexe.
- Sem branches `release`/`teste`/`prod`; ambiente e do Railway (ADR-016, emenda F4-0077).
- CI em todo PR e em todo push de `dev-v2`/`main-v2`/`main`.

## Pergunta em aberto: onde hospedar o front

### Opcao 1. Vercel (ADR-009, em uso)
- Previews por PR, CDN, deploy a cada push, plano gratuito para projeto pessoal.
- Plano Hobby nao permite uso comercial; produto vendido pede o plano Pro (pago). Conferir precos atuais.
- Front e API ficam em dominios diferentes.

### Opcao 2. Railway, servico com Dockerfile
- Servico `flow4tech-app` por ambiente: build com pnpm (`pnpm --filter @flow4tech/app build`), imagem final com Caddy servindo `dist` e `try_files {path} /index.html` (fallback de SPA). O `control` e rota do app (`/control`), entao e um unico servico.
- Deploy com `railway up --service flow4tech-app`, mais um job no `deploy.yml`.
- Custo: Railway cobra por uso (CPU, memoria, saida); um container estatico usa pouco, mas roda 24h por ambiente. Conferir precos atuais.
- Perde: preview por PR, CDN global (uma regiao so).
- Ganha: front e API no mesmo lugar e sob o mesmo dominio.
- O `vercel.json` ja define o fallback de SPA e as excecoes (`assets/`, `sw.js`, `workbox-`, `manifest.webmanifest`, icones); o Caddyfile precisa respeitar as mesmas excecoes (service worker nao pode cair no `index.html`).

### Opcao 3. Bucket (descartada por enquanto)
- O bucket do Railway e S3 privado (acesso por credencial). Nao serve site: sem endpoint estatico, sem fallback de SPA, sem CDN (nao confirmado na documentacao; o `--help` do CLI nao mostra nada de site).
- Os buckets planejados em `.scratch/S-00-novo-sistema/04-arquitetura.md` (linha 169) sao para anexos, XML de NF-e e dados frios, nao para o front.
- Front em bucket exigiria CDN na frente (ex.: Cloudflare R2 + Pages/Workers): provedor novo sem ganho sobre a Vercel.

## Ponto tecnico que vale em qualquer opcao: cookie de refresh
O cookie `__Host-f4t_refresh` e `SameSite=Strict` e Path=/. Front e API em sites diferentes (`*.vercel.app` e `*.up.railway.app`) fazem o navegador nao enviar o cookie nas chamadas do front a API, e a renovacao da sessao quebra. Saidas:
1. Dominio proprio, front e API sob o mesmo dominio registravel (`app.<dominio>` e `api.<dominio>`): contam como o mesmo site.
2. Sem dominio ainda: o front repassa as chamadas da API pela propria origem (rewrite/proxy da Vercel, ou rota no Caddy), de modo que o navegador veja um unico site.
3. Verificar tambem `Flow4Tech__Sessoes__AllowedOrigins__0`, `Origin` do guarda de CSRF e CORS para a origem do front.
O ADR-009 nao trata disso; precisa de resposta antes do front falar com a API em staging.

## Perguntas para decidir
- Ha dominio proprio (ou sera registrado)? Qual?
- O produto sera vendido (Vercel Pro) ou segue pessoal por enquanto?
- Preview por PR faz falta?
- Se mudar para o Railway: nova ADR (substitui o trecho de front do ADR-009) e ticket proprio.

## Proximos passos (basico, ja combinado)
1. Railway `staging`: `railway add --database postgres` e `railway add --service flow4tech-api`; variaveis (`APP_DB_PASSWORD`, `Flow4Tech__Tokens__AppKey`, `MIGRATION_DATABASE_URL`, `DATABASE_URL`, `Flow4Tech__Rede__ProxyConfiavel=true`); `railway domain`.
2. Repetir em `production`.
3. Dono: criar um project token por ambiente no painel (o CLI nao cria).
4. GitHub (`gh`): environments `staging` e `production`, secret `RAILWAY_TOKEN`, variaveis `RAILWAY_SERVICE` e `API_BASE_URL`; revisor obrigatorio em `production`.
5. Ticket: `deploy-staging` passa a disparar em push de `dev-v2`; `deploy-production` valida o SHA em `main-v2`.
6. Primeiro deploy de staging; conferir `/health/ready` com o `commit` certo; ajustar o `preDeployCommand` do `railway.toml` se preciso (marcado UNVERIFIED no runbook).
7. Depois: migrar `railway.toml` (deprecado, funciona ate 2026-12-01).

## Registro de 2026-11-07: o que aconteceu no primeiro staging (F4-0076 a F4-0080)
- Primeiro CI em `dev-v2` falhou no gitleaks (4 fixtures de teste falsas): resolvido com `.gitleaksignore` por fingerprint (F4-0079).
- Staging subiu com 503 em `/health/ready` mas o deploy contou como SUCCESS: o banco estava vazio. Causa: o Railway ignora `railway.toml` em servico que nunca usou Config as Code (opcao fechada em 2026-08-28), entao nao havia pre-deploy nem healthcheck.
- Diagnostico: o `serviceManifest` do deployment mostrava `preDeployCommand: null`; o painel nao tinha a etapa Pre-deploy. O CLI nao mostra logs de pre-deploy e `railway environment edit` nao grava campos de deploy ("No changes to apply").
- Correcao: `.railway/railway.ts` (gerado por `railway config migrate`), `plan` (0 add, 2 change, 0 destroy), `apply`, redeploy. Runbook reescrito.
- Tropecos de CLI: no zsh `$VAR` com varios argumentos nao e separado (usar array); nao esconder stderr ao gravar variaveis (uma tentativa falhou em silencio); `railway ssh` pede chave registrada e o host aceito uma vez em terminal real.
- Decisao: deploy pela integracao Git do Railway com "wait for CI" (`dev-v2` -> staging, `main-v2` -> production), sem workflow de deploy. Production sem aprovacao manual: a protecao e o PR em `main-v2` + check `gates` (configurar no GitHub).
- Production criado (PR #47 mergeado em `main-v2`, depois `railway config apply`, variaveis com segredos novos, dominio, redeploy). `/health/ready` 200 com o commit do merge, com o pre-deploy rodando sozinho. Detalhe: o nome do servico e unico por projeto e o 2o Postgres saiu como `Postgres-10ae` (as referencias de variavel usam esse nome).
- Pendente: protecao de branch no GitHub (PR e `gates` em `main-v2`); decisao sobre front/dominio.
