# F4-0132: Lista de comandas do app alinhada ao prototipo

**What to build:** A tela `/salao` (lista de comandas) segue o prototipo aprovado (`.scratch/S-00-novo-sistema/prototipos/index.html`, `#m=mobile`) antes do piloto em modo sombra: cabecalho de contexto (empresa, unidade, usuario da sessao, botao de tema), busca com icone, filtros em chips com contagem (Minhas, Todas, Abertas, Aguardando), cartao de comanda com nome/mesa, tempo aberta, garcom, valor (centavos, `formatBRL`) e estado em palavra (Aberta, Aguardando pagamento, Parada ha N min com icone), menu de baixo com icones e rotulos filtrado por permissao.
**Blocked by:** -
**Touches:** frontend/app/src/**, frontend/design-system/src/**, frontend/app/e2e/**, frontend/packages/api-client/src/schema.ts, backend/src/Api/**, backend/src/Comandas/**, backend/src/Organizacao/**, backend/openapi (contrato), backend/tests/**
**Nature:** ui (+ extensao read-only do backend: dados que a lista nao devolvia)
**tdd-scope:** required
**Seams:** vitest/testing-library (filtros, contagem, estados); Playwright a 390x844 e 360x740, claro e escuro; testes de integracao da rota (permitido, negado, outra organizacao).
**Status:** done

- [ ] Cabecalho com empresa, unidade e usuario vindos da sessao (nunca do cliente) e botao de tema.
- [ ] Busca com icone e filtros em chips com contagem.
- [ ] Cartao com valor, garcom, tempo e estado em palavra (cor nunca e o unico sinal).
- [ ] Menu de baixo com icones e rotulos, filtrado por permissao.
- [ ] Estados vazio, esqueleto, erro e offline cobertos por teste; axe estrito e alvos de 44 px.
