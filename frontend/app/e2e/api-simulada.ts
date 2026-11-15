import type { Page } from "@playwright/test";

/** The API answered inside the browser (`page.route`), so the salão specs of the tablet and the computer run with no backend. */
const base64url = (o: unknown) => Buffer.from(JSON.stringify(o)).toString("base64url");
const token = `${base64url({ alg: "HS256", typ: "JWT" })}.${base64url({ sub: "conta-1" })}.assinatura`;
const comanda = (n: number, nome: string, consumoCentavos: number) => ({
  id: `c${n}`,
  unidadeId: "u1",
  numero: n,
  numeroTexto: `#${n}`,
  identificacao: nome,
  mesaId: null,
  canal: "salao",
  garcomId: "conta-1",
  pessoas: null,
  estado: "aberta",
  taxaServicoPercentual: 10,
  taxaServicoRemovida: false,
  versao: 2,
  abertaEm: new Date(Date.now() - 20 * 60_000).toISOString(),
  garcomNome: "Marina",
  consumoCentavos,
  ultimaAtividadeEm: new Date(Date.now() - 2 * 60_000).toISOString(),
});
const item = {
  id: "i1",
  rodadaId: "r1",
  posicao: 1,
  produtoId: "p1",
  produtoNome: "Chopp 300ml",
  precoCentavos: 1250,
  quantidade: 2,
  observacao: null,
  estado: "lancado",
  motivoCancelamento: null,
  canceladoPor: null,
  canceladoEm: null,
};
const respostas: Record<string, unknown> = {
  "POST /refresh": {
    accessToken: token,
    expiraEmSegundos: 300,
    sessaoId: "0197e2e0-0000-7000-8000-000000000001",
    unidadeAtivaId: "0197e2e0-0000-7000-8000-0000000000a1",
    deveTrocarSenha: false,
    requerSelecaoDeUnidade: false,
    sessaoExpiraEm: "2099-12-01T00:00:00Z",
  },
  "GET /sessao/permissoes": {
    permissoes: [
      "comanda:ver",
      "comanda:ver-comandas-de-outros",
      "comanda:abrir",
      "comanda:lancar-item",
      "comanda:cancelar-item",
      "comanda:desfazer-rodada",
      "produto:ver",
      "caixa:ver",
    ],
  },
  "GET /sessao/contexto": { organizacao: "Bar do Zé", unidade: "Unidade Centro", usuario: "Marina" },
  "GET /comandas": [comanda(7, "Ana", 2500), comanda(8, "Bia", 8900), comanda(9, "Balcão 1", 4200)],
  "GET /mesas": [],
  "GET /comandas/c7/conta": {
    consumoCentavos: 2500,
    taxaServicoCentavos: 250,
    totalCentavos: 2750,
    pagoCentavos: 0,
    saldoCentavos: 2750,
    taxaServicoRemovida: false,
    taxaServicoPercentual: 10,
    versao: 2,
    estado: "aberta",
  },
  "GET /comandas/c7/itens": { itens: [item], consumoCentavos: 2500 },
  "GET /cardapio/categorias": [{ id: "cat1", nome: "Bebidas", ativa: true, versao: 1 }],
  "GET /cardapio/produtos": [
    {
      id: "p1",
      categoriaId: "cat1",
      codigo: "CHOPP",
      nome: "Chopp 300ml",
      precoCentavos: 1250,
      moeda: "BRL",
      ativo: true,
      versao: 1,
    },
  ],
  "GET /cardapio/produtos/p1/grupos-opcao": [],
  "GET /cardapio/produtos/frequentes": [],
  "GET /cardapio/produtos/favoritos": [],
};
const rotasDaApi = /^\/(login|refresh|logout|sessao|caixa|cardapio|comandas|mesas|configuracoes|pagamentos)(\/|$)/;

export async function simularApi(page: Page) {
  await page.route(
    (url) => rotasDaApi.test(url.pathname),
    (route) => {
      const url = new URL(route.request().url());
      const corpo = respostas[`${route.request().method()} ${url.pathname}`];
      return corpo === undefined ? route.fulfill({ status: 404, body: "" }) : route.fulfill({ json: corpo });
    },
  );
}

export async function semAnimacao(page: Page) {
  await page.addStyleTag({
    content: "*, *::before, *::after { transition: none !important; animation: none !important; }",
  });
}
