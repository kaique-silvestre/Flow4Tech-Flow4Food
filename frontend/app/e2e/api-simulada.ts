import type { Page } from "@playwright/test";

/** The API answered inside the browser (`page.route`), so the salão specs of the tablet and the computer run with no backend. */
const base64url = (o: unknown) => Buffer.from(JSON.stringify(o)).toString("base64url");
const token = `${base64url({ alg: "HS256", typ: "JWT" })}.${base64url({ sub: "conta-1" })}.assinatura`;
export const comanda = (n: number, nome: string, consumoCentavos: number) => ({
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
  "GET /comandas/central": [
    comanda(7, "Ana", 2500),
    comanda(8, "Bia", 8900),
    {
      ...comanda(9, "Balcão 1", 4200),
      garcomId: "conta-2",
      garcomNome: "Rafael",
      ultimaAtividadeEm: new Date(Date.now() - 55 * 60_000).toISOString(),
    },
  ],
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
  // Registered after the generic route: Playwright tries the latest route first.
  // The central de comandas filters like the server does (waiter and number), so a spec sees the total change.
  await page.route(
    (url) => url.pathname === "/comandas/central",
    (route) => {
      const busca = new URL(route.request().url()).searchParams;
      const lista = (respostas["GET /comandas/central"] as unknown[]).filter((c) => {
        const { garcomId, numero } = c as { garcomId: string; numero: number };
        return (
          (!busca.get("garcomId") || busca.get("garcomId") === garcomId) &&
          (!busca.get("numero") || busca.get("numero") === String(numero))
        );
      });
      return route.fulfill({ json: { comandas: lista, total: lista.length } });
    },
  );
  await page.route(
    (url) => url.pathname === "/comandas/fluxo",
    // The live stream stays open and silent (the request is never answered); `simularFluxo` replaces it.
    () => new Promise<void>(() => undefined),
  );
}

/**
 * The live stream of the comandas, driven by the test: each connection the app opens is answered with what `emitir` queued since the last one (or just the open comment) and ends,
 * so the app reconnects (about every second) and reads the next batch. `lista` answers `GET /comandas` from then on, so a changed version shows up when the stream says it did.
 */
export async function simularFluxo(page: Page, lista: () => unknown) {
  const fila: string[] = [];
  let caido = false;
  await page.route(
    (url) => url.pathname === "/comandas/fluxo",
    (route) => {
      if (caido) {
        return route.fulfill({ status: 503, body: "" });
      }

      const resync = `event: resync\ndata: ${JSON.stringify({ motivo: "abertura", comandas: [] })}\n\n`;
      const corpo = `: aberto\n\n${resync}${fila.splice(0).join("")}`;
      return route.fulfill({ status: 200, contentType: "text/event-stream", body: corpo });
    },
  );
  await page.route(
    (url) => url.pathname === "/comandas",
    (route) => route.fulfill({ json: lista() }),
  );
  return {
    derrubar: (valor: boolean) => {
      caido = valor;
    },
    emitir: (nome: string, dados: unknown, id?: string) =>
      void fila.push(`${id ? `id: ${id}\n` : ""}event: ${nome}\ndata: ${JSON.stringify(dados)}\n\n`),
  };
}

export async function semAnimacao(page: Page) {
  await page.addStyleTag({
    content: "*, *::before, *::after { transition: none !important; animation: none !important; }",
  });
}
