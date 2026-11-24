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
  pessoas: 2,
  estado: "aberta",
  taxaServicoPercentual: 10,
  taxaServicoRemovida: false,
  versao: 2,
  abertaEm: new Date(Date.now() - 5 * 60_000).toISOString(),
  observacao: null,
};
const conta = {
  consumoCentavos: 2500,
  taxaServicoCentavos: 250,
  totalCentavos: 2750,
  pagoCentavos: 1000,
  saldoCentavos: 1750,
  taxaServicoRemovida: false,
  taxaServicoPercentual: 10,
  versao: 2,
  estado: "aberta",
  descontoCentavos: 0,
};
const pagamento = {
  id: "p1",
  comandaId: "c1",
  turnoId: "t1",
  metodo: "dinheiro",
  recebidoCentavos: 1000,
  aplicadoCentavos: 1000,
  trocoCentavos: 0,
  saldoRestanteCentavos: 1750,
  comandaVersao: 2,
  registradoEm: "2026-11-23T15:00:00Z",
  operadorId: "o1",
  metodoId: "m1",
  metodoNome: "Dinheiro",
  gorjetaCentavos: 0,
  estado: "confirmado",
  divisaoId: null,
  divisaoParte: null,
};
const estornos = (saldo: number) => ({
  pagamentoId: "p1",
  comandaId: "c1",
  aplicadoCentavos: 1000,
  estornadoCentavos: 1000 - saldo,
  saldoEstornavelCentavos: saldo,
  estornos: [],
});

type Rotas = Record<string, Response | ((c: never) => Response | Promise<Response>)>;

async function abrir(permissoes: string[], extra: Rotas = {}) {
  const chamadas = stubRede({
    "POST /refresh": json(sessaoJson()),
    "GET /sessao/permissoes": json({ permissoes }),
    "GET /sessao/regras-de-desconto": json({ desconto: false, cortesia: false, brinde: false }),
    "GET /comandas": json([comandaJson]),
    "GET /comandas/c1/conta": json(conta),
    "GET /comandas/c1/itens": json({ itens: [], consumoCentavos: 2500 }),
    "GET /comandas/c1/pagamentos": json([pagamento]),
    "GET /pagamentos/p1/estornos": json(estornos(1000)),
    "GET /cardapio/produtos": json([]),
    "GET /cardapio/categorias": json([]),
    "GET /cardapio/produtos/frequentes": json([]),
    "GET /cardapio/produtos/favoritos": json([]),
    ...extra,
  } as Rotas as never);
  await renderEm("/salao/comanda/c1");
  await screen.findByRole("heading", { level: 2, name: "Ana" });
  return chamadas;
}

async function abrirJanela(user: ReturnType<typeof userEvent.setup>) {
  await user.click(await screen.findByRole("button", { name: "Mais" }));
  await user.click(await screen.findByRole("button", { name: "Estornar pagamento" }));
  return within(await screen.findByRole("dialog", { name: "Estornar pagamento" }));
}

const corpo = (chamadas: { method: string; path: string; body?: unknown }[]) =>
  chamadas.find((c) => c.method === "POST" && c.path.endsWith("/estornos"))?.body;

describe("estorno window", () => {
  it("is absent for a person who cannot see payments", async () => {
    await abrir(["comanda:ver", "comanda:renomear"]);
    const user = userEvent.setup();
    await user.click(await screen.findByRole("button", { name: "Mais" }));
    expect(screen.queryByRole("button", { name: "Estornar pagamento" })).toBeNull();
  });

  it("refunds the whole balance with a reason and sends no value", async () => {
    const chamadas = await abrir(["comanda:ver", "pagamento:ver"], {
      "POST /pagamentos/p1/estornos": json({ estorno: { id: "e1" }, saldoEstornavelCentavos: 0 }, { status: 201 }),
    });
    const user = userEvent.setup();
    const janela = await abrirJanela(user);

    expect(janela.getByRole("button", { name: "Estornar" })).toBeDisabled();
    await user.click(await janela.findByRole("button", { name: "Erro de digitação" }));
    await user.click(janela.getByRole("button", { name: "Estornar" }));

    await waitFor(() => expect(corpo(chamadas)).toEqual({ valorCentavos: null, motivo: "Erro de digitação" }));
    await waitFor(() => expect(screen.queryByRole("dialog", { name: "Estornar pagamento" })).toBeNull());
  });

  it("refunds a typed partial value", async () => {
    const chamadas = await abrir(["comanda:ver", "pagamento:ver"], {
      "POST /pagamentos/p1/estornos": json({ estorno: { id: "e1" }, saldoEstornavelCentavos: 500 }, { status: 201 }),
    });
    const user = userEvent.setup();
    const janela = await abrirJanela(user);

    await user.click(await janela.findByRole("button", { name: "Parcial" }));
    for (const d of "500") {
      await user.click(janela.getByRole("button", { name: d }));
    }
    await user.click(janela.getByRole("button", { name: "Decisão da gerência" }));
    await user.click(janela.getByRole("button", { name: "Estornar" }));

    await waitFor(() => expect(corpo(chamadas)).toEqual({ valorCentavos: 500, motivo: "Decisão da gerência" }));
  });

  it("a refusal above the limit says only that the value is not allowed and keeps the window open", async () => {
    await abrir(["comanda:ver", "pagamento:ver"], {
      "POST /pagamentos/p1/estornos": json({ codigo: "negado" }, { status: 403 }),
    });
    const user = userEvent.setup();
    const janela = await abrirJanela(user);

    await user.click(await janela.findByRole("button", { name: "Cobrança em dobro" }));
    await user.click(janela.getByRole("button", { name: "Estornar" }));

    expect(await janela.findByRole("alert")).toHaveTextContent("Você não tem permissão para estornar este valor.");
    expect(screen.getByRole("dialog", { name: "Estornar pagamento" })).toBeInTheDocument();
  });

  it("says there is nothing to refund when everything was already refunded", async () => {
    await abrir(["comanda:ver", "pagamento:ver"], { "GET /pagamentos/p1/estornos": json(estornos(0)) });
    const user = userEvent.setup();
    const janela = await abrirJanela(user);

    expect(await janela.findByText("Esta comanda não tem pagamento para estornar.")).toBeInTheDocument();
  });
});
