import { readFileSync } from "node:fs";
import { resolve } from "node:path";
import { CORTE_TABLET } from "@flow4tech/design-system";
import { screen, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { json, renderEm, sessaoJson, stubRede } from "../../test-utils";

/** Visual reference: .scratch/S-00-novo-sistema/prototipos/index.html#m=web&open=3 (list and 420 px detail panel side by side); #m=mobile stays one column. */
function larguraDaJanela(px: number) {
  Object.defineProperty(window, "innerWidth", { configurable: true, writable: true, value: px });
}

beforeEach(() => larguraDaJanela(820));
afterEach(() => {
  vi.unstubAllGlobals();
  larguraDaJanela(1024);
});

const comanda = (n: number, nome: string) => ({
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
  abertaEm: new Date(Date.now() - 5 * 60_000).toISOString(),
  garcomNome: "Marina",
  consumoCentavos: 2500,
  ultimaAtividadeEm: new Date(Date.now() - 60_000).toISOString(),
});
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
};
const rede = () =>
  stubRede({
    "POST /refresh": json(sessaoJson()),
    "GET /comandas": json([comanda(7, "Ana"), comanda(8, "Bia")]),
    "GET /mesas": json([]),
    "GET /comandas/:id/conta": json(conta),
    "GET /comandas/:id/itens": json({ itens: [], consumoCentavos: 0 }),
    "GET /cardapio/categorias": json([]),
    "GET /cardapio/produtos": json([]),
    "GET /cardapio/produtos/frequentes": json([]),
    "GET /cardapio/produtos/favoritos": json([]),
  });

describe("salão on the tablet band", () => {
  it("shows the list and the open comanda side by side, the open one marked current, the detail in the panel region", async () => {
    rede();

    await renderEm("/salao/comanda/c7");

    const lista = await screen.findByRole("list", { name: "Comandas abertas" });
    const painel = screen.getByRole("region", { name: "Comanda aberta" });
    expect(within(lista).getByRole("link", { name: /Ana/ })).toHaveAttribute("aria-current", "page");
    expect(await within(painel).findByRole("heading", { level: 2, name: "Ana" })).toBeVisible();
  });

  it("opens another comanda in the panel from the list, keeping the list on screen", async () => {
    rede();
    const { router } = await renderEm("/salao/comanda/c7");
    await screen.findByRole("heading", { level: 2, name: "Ana" });

    await userEvent.click(
      within(screen.getByRole("list", { name: "Comandas abertas" })).getByRole("link", { name: /Bia/ }),
    );

    expect(await screen.findByRole("heading", { level: 2, name: "Bia" })).toBeVisible();
    expect(router.state.location.pathname).toBe("/salao/comanda/c8");
    expect(screen.getByRole("list", { name: "Comandas abertas" })).toBeVisible();
  });

  it("asks for a comanda in the panel while none is open", async () => {
    rede();

    await renderEm("/salao");

    expect(await screen.findByRole("list", { name: "Comandas abertas" })).toBeVisible();
    expect(
      within(screen.getByRole("region", { name: "Comanda aberta" })).getByText("Escolha uma comanda para ver a conta."),
    ).toBeVisible();
  });

  it("starts at the tablet cut and not before", async () => {
    rede();
    larguraDaJanela(CORTE_TABLET - 1);

    await renderEm("/salao/comanda/c7");

    expect(await screen.findByRole("heading", { level: 2, name: "Ana" })).toBeVisible();
    expect(screen.queryByRole("list", { name: "Comandas abertas" })).toBeNull();
    expect(screen.queryByRole("region", { name: "Comanda aberta" })).toBeNull();
  });

  it("leaves the phone list as one column with no panel", async () => {
    rede();
    larguraDaJanela(390);

    await renderEm("/salao");

    expect(await screen.findByRole("list", { name: "Comandas abertas" })).toBeVisible();
    expect(screen.queryByRole("region", { name: "Comanda aberta" })).toBeNull();
  });
});

describe("the stylesheet and the band constant agree", () => {
  it("turns the bottom sheet into a side panel from the tablet cut", () => {
    const sistema = readFileSync(resolve(__dirname, "../../../../design-system/src/styles.css"), "utf8");

    expect(sistema).toMatch(new RegExp(`@media \\(min-width: ${CORTE_TABLET}px\\)\\s*\\{\\s*\\.f4-sheet\\s*\\{`));
  });
});
