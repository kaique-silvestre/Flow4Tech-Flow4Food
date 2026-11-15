# 019. Faixas de tela: celular, tablet e computador

- Status: proposed
- Date: 2026-11-15

## Context
O S-05 (bloco R) faz o app todo funcionar em celular, tablet e computador com o mesmo conteúdo e a mesma função. Os 600 e 1024 px do rascunho 1 do spec eram provisórios. Regra do dono (2026-11-15): uso comercial, tamanhos padrão de mercado, sem aparelho de referência. Os limites reais saem do CSS do protótipo `.scratch/S-00-novo-sistema/prototipos/index.html` (`#m=mobile` e `#m=web`), a única referência visual (F4-0133).

O que o protótipo mostra (linhas do arquivo):
- Celular: `@media (max-width:499.98px)`, a moldura do celular ocupa a tela (l. 119 e 2463). Acima disso a coluna do celular é centrada com 390 px (`--phone-w`, l. 105).
- Menu: `.app{--sw:264px}` (l. 865); a 1180 px ou menos `--sw:200px` (l. 1050); modo trilho (`body.rail`, `--sw:72px`, atalho `[`, l. 866, 7369); o trilho liga sozinho com `matchMedia('(max-width:899.98px)')` quando o usuário não escolheu (l. 8340).
- Painel lateral de detalhe: 420 px (l. 1010), 380 px a 1280 px ou menos (l. 1048).
- Janela da comanda: uma coluna a 900 px ou menos (l. 1354). Quadro e divisão do salão: uma coluna a 1100 px ou menos (l. 1943); cabeçalho da lista some a 1100 px (l. 1879).
- Barra de abas inferior no celular: `.tabbar` (l. 2269).
- O protótipo NÃO tem uma consulta de mídia em 1000 px; a faixa "compactação entre 1000 e 1280" é a regra do dono que agrupa as consultas de 1100, 1180 e 1280.

## Decision
Três faixas pela largura da janela (px CSS), mobile-first:

| Faixa | Largura | Origem |
| --- | --- | --- |
| Celular | menor que 500 | protótipo l. 119 (`max-width:499.98px`) |
| Tablet | 500 a 999 | piso do celular acima; teto 1000 = início da compactação do web (regra do dono) |
| Computador | 1000 ou mais, com compacto de 1000 a 1279 e pleno a partir de 1280 | protótipo l. 1048, 1050, 1943 (1280, 1180, 1100) |

Os valores de corte são constantes únicas de código (`500`, `1000`, `1280`); nenhuma tela usa outro número. Cobrem comercialmente: celulares em pé, tablets de 7 a 11 pol. em pé e deitado de até 999 px, notebooks e monitores. Nenhum aparelho é citado como referência.

Navegação por faixa, copiando o protótipo: celular, barra de abas inferior; tablet, trilho de 72 px; computador, menu de 200 px (compacto) ou 264 px (pleno), recolhível ao trilho com `[`. A escolha manual do usuário (trilho ou menu) vale sobre a automática, como no protótipo (`flow.sidebar`, l. 8338).

Os demais detalhes (folhas viram painel ou janela, densidade, teclado, foco) ficam na seção "Several screens and web management" do `docs/design/DESIGN.md`.

## Consequences
- Um único conjunto de cortes: F4-0135 a F4-0138 e as telas do bloco G leem as mesmas constantes.
- Tablet em pé e deitado até 999 px usa o trilho e uma coluna de detalhe ao lado da lista (R3); tablets deitados de 1024 px ou mais caem no computador compacto. Aceito: é o padrão comercial de 1024.
- O protótipo muda de comportamento em 900 px (trilho automático e janela em uma coluna), dentro do nosso tablet. Seguimos a regra do dono (tablet até 999 px com trilho) e registramos a diferença como pergunta ao dono, em vez de inventar um corte novo.
- Os cortes de 1100 e 1180 px do protótipo viram ajustes internos do computador compacto, não faixas novas.

## Alternatives considered
- Manter 600 e 1024 do rascunho: números sem origem no protótipo; descartado pelo dono.
- Quatro ou mais faixas (celular grande, tablet pequeno): mais variantes por tela, sem base no protótipo.
- Cortes por aparelho (iPad, Galaxy Tab): proibido pela regra do dono.
- Numeração: 017 está reservado para Clientes/Promoções e 018 já existe (desconto e cortesia); 019 é o próximo número livre, e o ticket F4-0134 já aponta `019-*.md`.
