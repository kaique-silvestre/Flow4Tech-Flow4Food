import { describe, expect, it } from "vitest";
import { haFiltro, juntarGarcons, lerFiltros, paraBusca, paraConsulta, soDigitos, textoDoTotal } from "./filtros";

const vazio = { garcom: "", mesa: "", tempo: "", numero: "", vista: "cartoes" } as const;

describe("filters of the central (address and query)", () => {
  it("reads nothing as no filter, cards view", () => {
    expect(lerFiltros({})).toEqual(vazio);
    expect(haFiltro(lerFiltros({}))).toBe(false);
  });

  it("reads the address and turns it into the server's query, only what is filled", () => {
    const filtros = lerFiltros({ garcom: "g1", mesa: "m4", tempo: "45", numero: "12", vista: "tabela" });

    expect(filtros.vista).toBe("tabela");
    expect(paraConsulta(filtros)).toEqual({ garcomId: "g1", mesaId: "m4", tempoMinimoMinutos: "45", numero: "12" });
    expect(paraConsulta(lerFiltros({ garcom: "g1" }))).toEqual({ garcomId: "g1" });
    expect(haFiltro(filtros)).toBe(true);
  });

  it("keeps the view out of the query and the default view out of the address", () => {
    expect(paraConsulta(lerFiltros({ vista: "tabela" }))).toEqual({});
    expect(paraBusca(vazio)).toEqual({});
    expect(paraBusca(lerFiltros({ garcom: "g1", vista: "tabela", numero: "7" }))).toEqual({
      garcom: "g1",
      numero: "7",
      vista: "tabela",
    });
  });

  it("drops what is not a whole number from minutes and number, and ignores unknown values", () => {
    expect(soDigitos("12abc")).toBe("12");
    expect(soDigitos("-5")).toBe("5");
    expect(soDigitos("007")).toBe("7");
    expect(soDigitos("0")).toBe("0");
    expect(lerFiltros({ tempo: "x", numero: ["1"], vista: "mapa", garcom: 3 })).toEqual({ ...vazio, garcom: "3" });
  });
});

describe("the count line", () => {
  it("counts in the singular and the plural", () => {
    expect(textoDoTotal(0, 0)).toBe("0 comandas abertas");
    expect(textoDoTotal(1, 1)).toBe("1 comanda aberta");
    expect(textoDoTotal(9, 9)).toBe("9 comandas abertas");
  });

  it("says when the server capped the rows", () => {
    expect(textoDoTotal(230, 200)).toBe("230 comandas abertas, mostrando as 200 mais recentes");
  });
});

describe("the waiters the select offers", () => {
  const comanda = (garcomId: string, garcomNome: string | null) =>
    ({ garcomId, garcomNome }) as Parameters<typeof juntarGarcons>[1][number];

  it("keeps the ones seen before when the rows narrow to one, sorted by name", () => {
    const antes = juntarGarcons([], [comanda("g2", "Rafael"), comanda("g1", "Marina")], "");
    expect(antes.map((g) => g.nome)).toEqual(["Marina", "Rafael"]);

    const depois = juntarGarcons(antes, [comanda("g2", "Rafael")], "g2");
    expect(depois.map((g) => g.id)).toEqual(["g1", "g2"]);
  });

  it("still offers a waiter chosen by the address that no row shows yet", () => {
    expect(juntarGarcons([], [], "g9")).toEqual([{ id: "g9", nome: "Garçom escolhido" }]);
  });
});
