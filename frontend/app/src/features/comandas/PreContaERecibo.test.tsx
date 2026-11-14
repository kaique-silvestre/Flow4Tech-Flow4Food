import { onlineManager } from "@tanstack/react-query";
import { screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, describe, expect, it, vi } from "vitest";
import { json, renderEm, sessaoJson, stubRede } from "../../test-utils";

afterEach(() => {
  vi.unstubAllGlobals();
  Object.defineProperty(window.navigator, "onLine", { configurable: true, value: true });
  onlineManager.setOnline(true);
});

const comandaJson = {
  id: "c1",
  unidadeId: "u1",
  numero: 7,
  numeroTexto: "#7",
  identificacao: "Ana",
  mesaId: null,
  canal: "salao",
  garcomId: "g1",
  pessoas: null,
  estado: "aberta",
  taxaServicoPercentual: 10,
  taxaServicoRemovida: false,
  versao: 2,
  abertaEm: new Date().toISOString(),
  observacao: null,
  contaPedidaEm: null,
};
const conta = {
  consumoCentavos: 2500,
  taxaServicoCentavos: 250,
  totalCentavos: 2750,
  pagoCentavos: 0,
  saldoCentavos: 2750,
  taxaServicoRemovida: false,
  taxaServicoPercentual: 10,
  versao: 2,
  estado: "aberta",
  descontoCentavos: 0,
};
const documento = (extra: Record<string, unknown> = {}) => ({
  tipo: "pre-conta",
  segundaVia: false,
  aviso: "Sem valor fiscal",
  numero: 7,
  numeroTexto: "#7",
  identificacao: "Ana",
  diaOperacional: "2026-11-14",
  abertaEm: new Date().toISOString(),
  fechadaEm: null,
  contaPedidaEm: null,
  geradaEm: new Date().toISOString(),
  itens: [
    {
      descricao: "Chopp 300ml",
      quantidade: 2,
      unitarioCentavos: 1250,
      totalCentavos: 2500,
      tipo: "normal",
      observacao: null,
      opcoes: [],
    },
  ],
  descontos: [],
  conta: {
    consumoCentavos: 2500,
    descontoCentavos: 0,
    taxaServicoCentavos: 250,
    taxaServicoRemovida: false,
    taxaServicoPercentual: 10,
    totalCentavos: 2750,
    pagoCentavos: 0,
    saldoCentavos: 2750,
  },
  pagamentos: [],
  partes: null,
  ...extra,
});
const texto = (t: string) => new Response(t, { headers: { "content-type": "text/plain" } });
const n = (t: string | null | undefined) => (t ?? "").replace(/ /g, " ");
const VER = ["comanda:ver", "mesa:ver", "produto:ver"];
const CAIXA = [...VER, "comanda:reimprimir"];

function rotas(extra: Record<string, unknown> = {}, permissoes = VER, comandas: unknown[] = [comandaJson]) {
  return {
    "POST /refresh": json(sessaoJson()),
    "GET /sessao/permissoes": json({ permissoes }),
    "GET /sessao/regras-de-desconto": json({ desconto: false, cortesia: false, brinde: false }),
    "GET /comandas": json(comandas),
    "GET /comandas/c1/conta": json(conta),
    "GET /comandas/c1/itens": json({ itens: [], consumoCentavos: 2500 }),
    "GET /cardapio/produtos": json([]),
    "GET /cardapio/categorias": json([]),
    "GET /cardapio/produtos/frequentes": json([]),
    "GET /cardapio/produtos/favoritos": json([]),
    ...extra,
  } as never;
}

async function abrirPreConta(
  extra: Record<string, unknown> = {},
  permissoes = VER,
  comandas: unknown[] = [comandaJson],
) {
  const chamadas = stubRede(rotas(extra, permissoes, comandas));
  await renderEm("/salao/comanda/c1");
  await screen.findByRole("heading", { level: 2, name: "Ana" });
  await userEvent.click(screen.getByRole("button", { name: /Ver pré-conta/ }));
  return { chamadas, folha: await screen.findByRole("dialog", { name: "Pré-conta" }) };
}

describe("pre-bill", () => {
  it("shows the server's numbers, items and the not-fiscal notice", async () => {
    const { folha } = await abrirPreConta({ "GET /comandas/c1/pre-conta": json(documento()) });

    expect(await within(folha).findByText("Chopp 300ml")).toBeVisible();
    expect(within(folha).getByText("Sem valor fiscal")).toBeVisible();
    expect(n(within(folha).getByLabelText("Conta").textContent)).toContain("R$ 27,50");
  });

  it("shows loading, then the error with retry when the server fails", async () => {
    let tentativa = 0;
    const { folha } = await abrirPreConta({
      "GET /comandas/c1/pre-conta": () => (++tentativa === 1 ? json({}, { status: 500 }) : json(documento())),
    });

    expect(await within(folha).findByText("Não foi possível carregar a pré-conta.")).toBeVisible();
    await userEvent.click(within(folha).getByRole("button", { name: "Tentar novamente" }));
    expect(await within(folha).findByText("Chopp 300ml")).toBeVisible();
  });

  it("403 does not say why and offers no retry", async () => {
    const { folha } = await abrirPreConta({ "GET /comandas/c1/pre-conta": new Response(null, { status: 403 }) });

    expect(await within(folha).findByText("Você não tem permissão para ver a pré-conta.")).toBeVisible();
    expect(within(folha).queryByRole("button", { name: "Tentar novamente" })).toBeNull();
  });

  it("404 and 409 get their own words", async () => {
    const { folha } = await abrirPreConta({
      "GET /comandas/c1/pre-conta": json({ codigo: "comanda-nao-aberta" }, { status: 409 }),
    });
    expect(await within(folha).findByText("Esta comanda já não está aberta.")).toBeVisible();
  });

  it("says there is no connection when the request cannot be made", async () => {
    const { folha } = await abrirPreConta({
      "GET /comandas/c1/pre-conta": () => {
        throw new TypeError("Failed to fetch");
      },
    });
    expect(await within(folha).findByText(/Sem conexão com o servidor/)).toBeVisible();
  });

  it("shares the server's text through the share sheet", async () => {
    const share = vi.fn().mockResolvedValue(undefined);
    Object.defineProperty(window.navigator, "share", { configurable: true, value: share });
    const { folha } = await abrirPreConta({
      "GET /comandas/c1/pre-conta": json(documento()),
      "GET /comandas/c1/pre-conta/texto": texto("PRE-CONTA #7\nTotal 27,50"),
    });
    await within(folha).findByText("Chopp 300ml");

    await userEvent.click(within(folha).getByRole("button", { name: "Compartilhar" }));

    await waitFor(() => expect(share).toHaveBeenCalledWith({ title: "Pré-conta", text: "PRE-CONTA #7\nTotal 27,50" }));
    Object.defineProperty(window.navigator, "share", { configurable: true, value: undefined });
  });

  it("copies the text when the phone has no share sheet", async () => {
    Object.defineProperty(window.navigator, "share", { configurable: true, value: undefined });
    const writeText = vi.fn().mockResolvedValue(undefined);
    Object.defineProperty(window.navigator, "clipboard", { configurable: true, value: { writeText } });
    const { folha } = await abrirPreConta({
      "GET /comandas/c1/pre-conta": json(documento()),
      "GET /comandas/c1/pre-conta/texto": texto("PRE-CONTA #7"),
    });
    await within(folha).findByText("Chopp 300ml");

    await userEvent.click(within(folha).getByRole("button", { name: "Compartilhar" }));

    expect(await within(folha).findByText("Texto copiado. Cole onde quiser enviar.")).toBeVisible();
    expect(writeText).toHaveBeenCalledWith("PRE-CONTA #7");
  });

  it("marks the bill as requested and the button says so", async () => {
    const { chamadas, folha } = await abrirPreConta({
      "GET /comandas/c1/pre-conta": json(documento()),
      "POST /comandas/c1/pedir-conta": json(
        { ...comandaJson, contaPedidaEm: new Date().toISOString() },
        { status: 201 },
      ),
    });
    await within(folha).findByText("Chopp 300ml");

    await userEvent.click(within(folha).getByRole("button", { name: "Pedir a conta" }));

    const pedida = await within(folha).findByRole("button", { name: /^Conta pedida/ });
    expect(pedida).toBeDisabled();
    expect(chamadas.filter((c) => c.method === "POST" && c.path === "/comandas/c1/pedir-conta")).toHaveLength(1);
  });

  it("shows 'conta pedida' already when the server has the mark", async () => {
    const { folha } = await abrirPreConta(
      { "GET /comandas/c1/pre-conta": json(documento({ contaPedidaEm: new Date().toISOString() })) },
      VER,
      [{ ...comandaJson, contaPedidaEm: new Date().toISOString() }],
    );
    await within(folha).findByText("Chopp 300ml");

    expect(within(folha).getByRole("button", { name: /^Conta pedida/ })).toBeDisabled();
    expect(within(folha).queryByRole("button", { name: "Pedir a conta" })).toBeNull();
  });

  it("a refused request (409) keeps the sheet and says why", async () => {
    const { folha } = await abrirPreConta({
      "GET /comandas/c1/pre-conta": json(documento()),
      "POST /comandas/c1/pedir-conta": json({ codigo: "comanda-nao-aberta" }, { status: 409 }),
    });
    await within(folha).findByText("Chopp 300ml");

    await userEvent.click(within(folha).getByRole("button", { name: "Pedir a conta" }));

    expect(await within(folha).findByText("Esta comanda já não está aberta.")).toBeVisible();
    expect(within(folha).getByRole("button", { name: "Pedir a conta" })).toBeEnabled();
  });

  it("offline it keeps the document and blocks asking for the bill and sharing", async () => {
    const { folha } = await abrirPreConta({ "GET /comandas/c1/pre-conta": json(documento()) });
    await within(folha).findByText("Chopp 300ml");

    Object.defineProperty(window.navigator, "onLine", { configurable: true, value: false });
    window.dispatchEvent(new Event("offline"));

    expect(await within(folha).findByText(/Sem conexão\. Pedir a conta e compartilhar/)).toBeVisible();
    expect(within(folha).getByRole("button", { name: "Pedir a conta" })).toBeDisabled();
    expect(within(folha).getByRole("button", { name: "Compartilhar" })).toBeDisabled();
    expect(within(folha).getByText("Chopp 300ml")).toBeVisible();
  });

  it("the comanda header shows 'Aguardando conta' once requested", async () => {
    stubRede(rotas({}, VER, [{ ...comandaJson, contaPedidaEm: new Date().toISOString() }]));
    await renderEm("/salao/comanda/c1");
    await screen.findByRole("heading", { level: 2, name: "Ana" });

    expect(screen.getByText(/Aguardando conta/)).toBeVisible();
  });
});

describe("receipt", () => {
  const recibo = documento({
    tipo: "recibo",
    segundaVia: true,
    fechadaEm: new Date().toISOString(),
    pagamentos: [
      {
        metodoNome: "Pix",
        aplicadoCentavos: 2750,
        trocoCentavos: 0,
        gorjetaCentavos: 0,
        registradoEm: new Date().toISOString(),
        divisaoParte: null,
      },
    ],
    conta: { ...documento().conta, pagoCentavos: 2750, saldoCentavos: 0 },
  });
  const fechada = { "GET /comandas/c1/conta": json({ ...conta, estado: "fechada" }) };

  it("opens the second copy of a closed comanda for whoever may reissue it", async () => {
    stubRede(rotas({ ...fechada, "GET /comandas/c1/recibo": json(recibo) }, CAIXA, []));
    await renderEm("/salao/comanda/c1");

    expect(await screen.findByRole("heading", { level: 2, name: /Recibo/ })).toBeVisible();
    expect(screen.getByText("2ª via")).toBeVisible();
    expect(screen.getByText("Pix")).toBeVisible();
    expect(screen.getByText("Sem valor fiscal")).toBeVisible();
  });

  it("without comanda:reimprimir there is no receipt: the old message stays and nothing is requested", async () => {
    const chamadas = stubRede(rotas(fechada, VER, []));
    await renderEm("/salao/comanda/c1");

    expect(await screen.findByText(/não está mais aberta/)).toBeVisible();
    expect(chamadas.some((c) => c.path.includes("/recibo"))).toBe(false);
  });

  it("an open comanda (409) says it is not closed yet, and a missing one says not found", async () => {
    stubRede(
      rotas(
        { ...fechada, "GET /comandas/c1/recibo": json({ codigo: "comanda-nao-fechada" }, { status: 409 }) },
        CAIXA,
        [],
      ),
    );
    await renderEm("/salao/comanda/c1");
    expect(await screen.findByText("Esta comanda ainda não foi fechada.")).toBeVisible();
  });

  it("404 and 403 are worded without detail", async () => {
    stubRede(
      rotas({ ...fechada, "GET /comandas/c1/recibo": json({ codigo: "nao-encontrada" }, { status: 404 }) }, CAIXA, []),
    );
    await renderEm("/salao/comanda/c1");
    expect(await screen.findByText("Comanda não encontrada.")).toBeVisible();
  });

  it("403 from the server shows the permission message", async () => {
    stubRede(rotas({ ...fechada, "GET /comandas/c1/recibo": new Response(null, { status: 403 }) }, CAIXA, []));
    await renderEm("/salao/comanda/c1");
    expect(await screen.findByText("Você não tem permissão para reabrir o recibo.")).toBeVisible();
  });

  it("offline it says so, with a retry", async () => {
    stubRede(
      rotas(
        {
          ...fechada,
          "GET /comandas/c1/recibo": () => {
            throw new TypeError("Failed to fetch");
          },
        },
        CAIXA,
        [],
      ),
    );
    await renderEm("/salao/comanda/c1");
    expect(await screen.findByText(/Sem conexão com o servidor/)).toBeVisible();
    expect(screen.getByRole("button", { name: "Tentar novamente" })).toBeVisible();
  });

  it("shares the receipt text", async () => {
    const writeText = vi.fn().mockResolvedValue(undefined);
    Object.defineProperty(window.navigator, "share", { configurable: true, value: undefined });
    Object.defineProperty(window.navigator, "clipboard", { configurable: true, value: { writeText } });
    stubRede(
      rotas(
        { ...fechada, "GET /comandas/c1/recibo": json(recibo), "GET /comandas/c1/recibo/texto": texto("RECIBO #7") },
        CAIXA,
        [],
      ),
    );
    await renderEm("/salao/comanda/c1");
    await screen.findByText("Pix");

    await userEvent.click(screen.getByRole("button", { name: "Compartilhar" }));

    await waitFor(() => expect(writeText).toHaveBeenCalledWith("RECIBO #7"));
  });
});
