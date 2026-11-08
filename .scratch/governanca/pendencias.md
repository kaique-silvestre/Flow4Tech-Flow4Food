# Pendencias de governanca (backlog)

Cada item tem o ID do indice `docs/tickets.md` (status `pendente`). Ao comecar um, mude o status no indice, crie a branch/commit com o ID e, ao terminar, marque `feito` e apague o item daqui.

## F4-0082 Actions em versoes com Node 24
- O CI avisa: `actions/checkout@v4` e `actions/setup-dotnet@v4` (e as demais) usam Node 20, depreciado; hoje sao forcadas a Node 24 e funcionam.
- Desde F4-0091 as actions estao fixadas por SHA: a troca de versao e feita trocando SHA e comentario juntos (procedimento em `docs/runbooks/sast.md`, "Pinned actions"); inclua o `codeql-action` (v3 -> v4).
- Fazer: confirmar no GitHub as versoes novas de checkout, setup-dotnet, setup-node, pnpm/action-setup, upload-artifact; trocar no `.github/workflows/ci.yml`; push em `dev-v2` e ver o CI verde antes de levar para `main-v2`.
- Risco baixo; nao e urgente.

## F4-0083 Protecao de branch (decisao e configuracao do dono)
- Em Settings > Branches (ou rulesets), `main-v2`: exigir PR, exigir o check `gates`, bloquear push direto. `dev-v2`: permite push direto (o CI roda).
- Sem isso a promocao staging -> production nao e protegida (Railway nao tem aprovacao manual).
- Eu leio o estado atual com `gh api repos/kaique-silvestre/Flow4Tech-Flow4Food/branches/main-v2/protection`.

## F4-0084 Front e dominio
- Opcoes e riscos em `hospedagem-do-front-e-ambientes.md`. Perguntas: existe dominio proprio? produto vendido (Vercel Pro)? preview por PR faz falta?
- Bloqueia o front de falar com a API em staging (cookie `__Host-f4t_refresh` e `SameSite=Strict`). Resultado esperado: ADR nova (ou emenda do ADR-009) e ticket de implementacao.

## F4-0085 Alertas de custo em US$ 1 e US$ 3
- O Railway so aceita alerta de e-mail a partir de US$ 5 (ja criado, alvo workspace). Para US$ 1 e US$ 3: workflow agendado (`cron`) que roda `railway usage projects --json` e abre issue ou falha o job acima do limiar.
- Precisa de um token do Railway no GitHub, criado pelo dono no painel (o CLI nao cria). Ate la, olhar `railway usage` manualmente.

## F4-0090 SBOM e scan do front (depende de F4-0084)
- Hoje o SBOM CycloneDX (F4-0089) e o Trivy cobrem so a imagem da API/migrator. O front nao entra: ainda nao sabemos onde sera hospedado (F4-0084).
- Se for estatico na Vercel/bucket (nao ha imagem): gerar a lista de pacotes do `frontend/pnpm-lock.yaml`/raiz com `trivy fs --format cyclonedx --output sbom-front.cdx.json .` (ou `--scanners vuln` para vulnerabilidades) e guardar como artefato do CI, no job `trivy` ou em um novo.
- Se for um container (ex.: nginx no Railway): o `Dockerfile` do front passa a ser coberto por `trivy config` e `trivy image` e pelo SBOM da imagem, como a API.
- Fazer so depois da decisao do F4-0084; `pnpm audit` ja cobre vulnerabilidades de pacotes do front enquanto isso. Detalhes em `docs/runbooks/sast.md`, secao "SBOM".

## F4-0095 CSP completa do front (depende de F4-0084)
- Hoje o front so manda `Content-Security-Policy: frame-ancestors 'none'` (nao quebra nada). A CSP de verdade (`default-src 'self'`, `script-src 'self'`, `connect-src` com a origem da API, `img-src`, `manifest-src`, `worker-src 'self'` para o service worker do PWA) precisa saber a origem da API, que so existe depois da decisao de hospedagem.
- Fazer: com a origem definida, subir primeiro como `Content-Security-Policy-Report-Only` em `frontend/{app,control}/vercel.json`, abrir o app (login, comanda, fila offline, instalar PWA) e olhar o console; so entao trocar para `Content-Security-Policy`. Cuidado com o inline que o Vite/PWA injeta (`style-src`).
- Se o front for servido por outro host (nginx/bucket), os mesmos headers vao na configuracao dele. Detalhes em `docs/runbooks/sast.md`, secao "Security headers".

## F4-0096 Rate limit compartilhado e limite geral (ADIADO de proposito)
- Decisao do dono (2026-11-07): sem rate limit compartilhado por enquanto. Projeto pequeno, uma instancia da API, custo nao compensa agora. Registrada no ADR-011 ("Decision update").
- Hoje existe so o limitador de login em memoria (por conta e por endereco, bloqueio progressivo). Nao existe limite geral de requisicoes por IP/rota, nem limite que sobreviva a reinicio ou valha entre instancias.
- **Reavaliar quando** ocorrer qualquer um: segunda instancia da API (aqui vira obrigatorio), clientes pagantes ou dados reais, ataque ou trafego de login anormal nos logs, cadastro publico, e-mail/CAPTCHA em producao.
- Fazer entao: trocar a implementacao atras de `ILimitadorDeLogin` (Postgres ou Redis, comparacao no ADR-011) e adicionar o `RateLimiter` do ASP.NET para o limite geral por IP/rota; atrás do Railway exige `Flow4Tech__Rede__ProxyConfiavel=true`. Um teste de carga simples (k6) ajuda a calibrar os numeros.

## F4-0098 Backup de producao (ADIADO de proposito) e teste de restauracao
- Levantamento feito (F4-0097, `docs/runbooks/backup-e-restauracao.md`): cada ambiente tem UM volume de Postgres (~155 MB de 5 GB) e nada no repositorio configura backup. O CLI do Railway nao mostra backups.
- CONFIRMADO no painel (2026-11-07): o workspace NAO e Pro; a aba Backups mostra "No Backups". **Producao nao tem backup nenhum hoje.**
- **Decisao do dono (2026-11-07): opcao C, nada por enquanto** (sem dado real em producao, projeto pequeno). Risco aceito: RPO ilimitado.
- **Reavaliar quando** ocorrer qualquer um: primeiro cliente ou dado real em producao, migracao destrutiva planejada, segunda instancia da API (gatilho do F4-0096), assinatura de plano pago por outro motivo (ligar os backups do Railway junto), ou revisao trimestral (a cada 3 meses a partir de 2026-11-07, proxima em 2027-02-07).
- Quando reavaliar: opcoes A (Pro), B (dump logico criptografado para bucket fora do Railway) e C no runbook; B e quase gratis e funciona sem o Pro.
- Depois: job semanal no CI que faz `pg_dump`/`pg_restore` em dois bancos, roda o migrator e confere `schema_migrations`, RLS e a cadeia de auditoria; decidir copia fora do Railway. Exercicio de restauracao real de producao a cada trimestre.

## F4-0100 A11y do front e avisos do Biome
- O Biome (F4-0099) deixou como AVISO (nao bloqueia) `a11y/useAriaPropsSupportedByRole`: `aria-label` em `<p>`/`<div>` e ignorado por leitores de tela. Locais: `ComandaPage.tsx` (Total da conta), `FecharConta.tsx` (Valor a receber), `NovaComandaSheet.tsx` (entrada da comanda, com `aria-live`), `BarraDeFila.tsx` (Conexao e acoes pendentes). Testes unitarios (`getByLabelText`) e e2e Playwright (`getByLabel("Total da conta")`, `"Valor a receber"`) localizam por esses rotulos.
- Fazer: dar a cada um um papel que aceite nome (`role="group"`, ou `role="status"` no que tem `aria-live`), ou `<output>` onde couber, atualizar os testes juntos e rodar o e2e no CI (nao da para rodar o Playwright sem Docker local). Passar o `design-reviewer`/revisao de acessibilidade se mudar a leitura.
- Regra `useSemanticElements` esta DESLIGADA de proposito (`role="group"`/`region` com `aria-label` sao validos; `<fieldset>`/`<section>` mudariam estilo). Reavaliar so se o design system for refeito.
- Avisos que sobraram: 31x `!` nao nulo (quase todos em testes), 37 infos `useLiteralKeys`, 1 `noDescendingSpecificity` no CSS do design system. Limpar aos poucos; depois pode-se tornar avisos bloqueantes (`--error-on-warnings`).

## F4-0102 DAST autenticado e CORP
- O job `dast` (F4-0093) roda sem login: so ve 401 nas rotas protegidas. Primeiro CI (2026-11-07): 1 alerta baixo (`Cross-Origin-Resource-Policy` ausente), 2 informativos esperados (4xx sem sessao, `no-store`).
- Fazer: no job, rodar o seed (`docker compose --profile e2e run --rm seed`), `POST /login` (header `X-F4T-Csrf: 1`) como GARCOM (nao dono: menos rotas destrutivas), passar `Authorization: Bearer` ao ZAP (`-z` com replacer), subir a API do job com `Flow4Tech__Tokens__AccessMinutos=30` (o token de 5 min venceria no meio), excluir do scan `/logout`, `/sessoes/{id}/revogar` e `/sessao/trocar-senha`, e conferir no fim se o token ainda vale. Lembrar: o ZAP envia uma requisicao a cada rota do OpenAPI, inclusive POST/PUT/DELETE, mesmo em modo seguro.
- CORP: adicionar `Cross-Origin-Resource-Policy: same-site` em `SecurityHeadersMiddleware` (so afeta requisicoes `no-cors`; o fetch do app usa CORS) e estender `SecurityHeadersTests`.
- Scan ativo continua fora: testes de isolamento e `DoisTenantsEndpointsTests` cobrem melhor a autorizacao entre organizacoes.

## F4-0104 Piso de cobertura do back
- F4-0103 mede a cobertura do back (coverlet em cada um dos 9 jobs de teste, relatorio unificado no resumo da execucao e no artefato `coverage-back-merged`), mas SEM piso: os projetos de integracao precisam de Docker e nunca foram medidos localmente.
- Fazer: olhar o numero das primeiras execucoes verdes, escolher um piso um pouco abaixo (como no front: app 85/77/90/85, design-system 95/95/93/95), aplicar no job `coverage-back` (`reportgenerator` + conferir `Summary.txt`/Cobertura) e colocar o job no `gates`.
- Atencao: o numero junta projetos que se sobrepoem; dado o codigo gerado e as migracoes em SQL, o percentual absoluto importa menos que a queda. Excluir o que nao e codigo do produto (`Flow4Tech.E2eSeed`, spikes) se aparecer no relatorio.

## Para rever no FIM DA SESSAO de 2026-11-07 (combinado com o dono)
- [ ] **F4-0104** Piso de cobertura do back: ler o numero unificado do job `coverage-back` na primeira execucao verde do CI (o Docker local nao estava ligado, entao nao deu para medir) e definir o piso.
- [ ] **Push**: conferir se os commits locais de `dev-v2` foram enviados e se o CI ficou verde (jobs novos: `trivy`, `dast`, `codeql`, `lint-frontend`, `coverage-back`).
- [ ] **F4-0102** DAST autenticado e header CORP (adiado: "depois vemos").
- [ ] Decisoes adiadas de proposito, so conferir que os gatilhos continuam validos: rate limit compartilhado (F4-0096), backup de producao (F4-0098, sem Pro e sem dado real).
- Apague este bloco quando os itens forem resolvidos ou ganharem seu proprio ticket andando.

## Sem ticket (lembretes do dono)
- Acompanhar `railway usage` depois de alguns dias (4 servicos sempre ligados: 2 APIs e 2 bancos).
- Avisos de pacotes depreciados do pnpm (`glob`, `whatwg-encoding`): so subdependencias.

## F4-0107 (pendente): piso do teste de mutacao
Depois das primeiras execucoes do job `mutation-tests` (artefatos `mutation-*`), definir um piso por dominio e tornar o job bloqueante. Ver tambem os sobreviventes de Comandas/Pagamentos/Caixa/Cardapio para novas propriedades.

## Axe nos dialogos (pendente, sem ID ainda)
O scan de F4-0109 cobre so login, comandas e caixa (tela inicial). Faltam os dialogos e folhas que aparecem depois de uma acao (lancar item, fechar caixa, receber pagamento, cancelar item) e as telas de mesa e cardapio. Dono pediu para tratar depois. Pegar ID F4-NNNN livre ao comecar (0108 esta com a hospedagem do Vercel).

## F4-0111 (pendente): dividir o bundle do app por rota
O app entrega um unico chunk JS (569 kB bruto, 179 kB gzip) e o build avisa "chunks maiores que 500 kB". Dividir por rota (import dinamico do TanStack Router) e baixar os limites de `frontend/app/bundle-budget.json`. Lighthouse (desempenho e PWA) tambem pendente, como relatorio informativo. Orcamento do `control` quando ele ganhar build.

## Identidade sem piso de mutacao (decisao do dono, 2026-11-08)
Nota local 36,6% porque 78 mutantes so sao alcancados pelos testes com Docker. Sem piso por enquanto. Revisitar rodando o Stryker de Identidade com a suite de integracao (CI com Docker) ou separando o que e puro. F4-0107 cobre os outros 6 dominios.
