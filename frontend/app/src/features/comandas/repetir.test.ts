import { describe, expect, it } from "vitest";
import type { Produto } from "./cardapio";
import { motivoDoIgnorado, previaDaRepeticao, quandoFechou, textoDosIgnorados } from "./repetir";
import type { ItemDaConta } from "./rodada";

const produto = (id: string, extra: Partial<Produto> = {}): Produto => ({
  id,
  categoriaId: "c",
  codigo: id.toUpperCase(),
  nome: `Produto ${id}`,
  precoCentavos: 1000,
  ...extra,
});
const item = (id: string, produtoId: string, extra: Partial<ItemDaConta> = {}): ItemDaConta => ({
  id,
  rodadaId: "r1",
  produtoId,
  produtoNome: `Produto ${produtoId}`,
  precoCentavos: 900,
  quantidade: 2,
  observacao: null,
  estado: "lancado",
  opcoes: [],
  unitarioCentavos: 900,
  ...extra,
});

describe("preview of the repeat", () => {
  it("prices the lines at the menu of now (add-ons included) and totals them", () => {
    const opcao = { id: "o1", grupoId: "g", nome: "Bacon", precoCentavos: 500 };
    const previa = previaDaRepeticao(
      [item("i1", "p1"), item("i2", "p2", { opcoes: [opcao], quantidade: 1 })],
      [produto("p1"), produto("p2", { precoCentavos: 2000 })],
    );

    expect(previa.entram.map((l) => l.unitarioCentavos)).toEqual([1000, 2500]);
    expect(previa.totalCentavos).toBe(2 * 1000 + 2500);
    expect(previa.naoEntram).toEqual([]);
  });

  it("sets aside a product that is out ('acabou') or gone from the menu, never dropping it silently", () => {
    const previa = previaDaRepeticao(
      [item("i1", "p1"), item("i2", "p2"), item("i3", "p3")],
      [produto("p1", { indisponivel: true }), produto("p2")],
    );

    expect(previa.entram.map((l) => l.item.id)).toEqual(["i2"]);
    expect(previa.naoEntram.map((i) => i.id)).toEqual(["i1", "i3"]);
    expect(previa.totalCentavos).toBe(2000);
  });
});

describe("what the server left out", () => {
  it("names each item with the reason in the floor's words", () => {
    expect(motivoDoIgnorado("produto-inativo")).toBe("acabou ou saiu do cardápio");
    expect(motivoDoIgnorado("opcao-inativa")).toBe("um adicional não está disponível");
    expect(motivoDoIgnorado("opcao-invalida")).toBe("os adicionais mudaram");
    expect(motivoDoIgnorado("outro")).toBe("não pôde ser repetido");
    expect(
      textoDosIgnorados([
        { produtoNome: "Chopp", quantidade: 1, motivo: "produto-inativo" },
        { produtoNome: "Pastel", quantidade: 2, motivo: "opcao-inativa" },
      ]),
    ).toBe("Não entraram: 1× Chopp (acabou ou saiu do cardápio); 2× Pastel (um adicional não está disponível).");
    expect(textoDosIgnorados([])).toBe("");
  });
});

describe("when the table closed", () => {
  const agora = new Date(2026, 10, 8, 12, 0).getTime();

  it("says today, yesterday, days ago, then the date", () => {
    expect(quandoFechou(new Date(2026, 10, 8, 1, 0).toISOString(), agora)).toBe("hoje");
    expect(quandoFechou(new Date(2026, 10, 7, 23, 0).toISOString(), agora)).toBe("ontem");
    expect(quandoFechou(new Date(2026, 10, 5, 20, 0).toISOString(), agora)).toBe("há 3 dias");
    expect(quandoFechou(new Date(2026, 9, 20, 20, 0).toISOString(), agora)).toBe("20/10");
    expect(quandoFechou(null, agora)).toBe("");
    expect(quandoFechou("lixo", agora)).toBe("");
  });
});
