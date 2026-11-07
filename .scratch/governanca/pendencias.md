# Pendencias de governanca (backlog)

Cada item tem o ID do indice `docs/tickets.md` (status `pendente`). Ao comecar um, mude o status no indice, crie a branch/commit com o ID e, ao terminar, marque `feito` e apague o item daqui.

## F4-0082 Actions em versoes com Node 24
- O CI avisa: `actions/checkout@v4` e `actions/setup-dotnet@v4` (e as demais) usam Node 20, depreciado; hoje sao forcadas a Node 24 e funcionam.
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

## Sem ticket (lembretes do dono)
- Acompanhar `railway usage` depois de alguns dias (4 servicos sempre ligados: 2 APIs e 2 bancos).
- Avisos de pacotes depreciados do pnpm (`glob`, `whatwg-encoding`): so subdependencias.
