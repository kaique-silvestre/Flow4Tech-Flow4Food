import { describe, expect, it } from "vitest";
import type { GrupoDeOpcao, Produto } from "./cardapio";
import { alternar, exigeEscolha, maisUsados, minimoEfetivo, pendencias } from "./opcoes";
import {
  adicionar,
  adicionarConfigurado,
  definirQuantidade,
  normalizarObservacao,
  paraItensDaRodada,
  precoUnitario,
  substituirItem,
  totalEmPreparo,
} from "./rodada";

const produto: Produto = { id: "p1", categoriaId: "c", codigo: "X", nome: "Hambúrguer", precoCentavos: 3000 };
const ponto: GrupoDeOpcao = {
  id: "g1",
  nome: "Ponto",
  minimo: 1,
  maximo: 1,
  opcoes: [
    { id: "o1", nome: "Mal passado", precoCentavos: 0 },
    { id: "o2", nome: "Ao ponto", precoCentavos: 0 },
  ],
};
const extras: GrupoDeOpcao = {
  id: "g2",
  nome: "Extras",
  minimo: 0,
  maximo: 2,
  opcoes: [
    { id: "o3", nome: "Bacon", precoCentavos: 500 },
    { id: "o4", nome: "Queijo", precoCentavos: 400 },
    { id: "o5", nome: "Ovo", precoCentavos: 300 },
  ],
};

describe("add-on selection rules", () => {
  it("a required group needs a choice; an optional one does not; a group never demands more than it offers", () => {
    expect(exigeEscolha([ponto, extras])).toBe(true);
    expect(exigeEscolha([extras])).toBe(false);
    expect(minimoEfetivo({ ...ponto, minimo: 3 })).toBe(2);
    expect(pendencias([ponto, extras], [])).toEqual(["Escolha uma opção em Ponto."]);
    expect(pendencias([ponto, extras], alternar([], ponto, ponto.opcoes[0]!))).toEqual([]);
  });

  it("in a group of one the new pick replaces the old; a second tap removes; a full group refuses more", () => {
    let e = alternar([], ponto, ponto.opcoes[0]!);
    e = alternar(e, ponto, ponto.opcoes[1]!);
    expect(e.map((o) => o.id)).toEqual(["o2"]);
    e = alternar(e, ponto, ponto.opcoes[1]!);
    expect(e).toEqual([]);

    let x = alternar([], extras, extras.opcoes[0]!);
    x = alternar(x, extras, extras.opcoes[1]!);
    x = alternar(x, extras, extras.opcoes[2]!);
    expect(x.map((o) => o.id)).toEqual(["o3", "o4"]);
  });

  it("lists the most used first, only those still pickable, at most four", () => {
    const f = [
      { grupoId: "g2", opcao: extras.opcoes[1]!, lancamentos: 3 },
      { grupoId: "g2", opcao: extras.opcoes[0]!, lancamentos: 9 },
      { grupoId: "g2", opcao: { id: "gone", nome: "Sumiu", precoCentavos: 0 }, lancamentos: 99 },
    ];
    expect(maisUsados([ponto, extras], f).map((m) => m.opcao.id)).toEqual(["o3", "o4"]);
    expect(maisUsados([ponto, extras], f, 1)).toHaveLength(1);
  });
});

describe("staged items with add-ons", () => {
  const bacon = { id: "o3", grupoId: "g2", nome: "Bacon", precoCentavos: 500 };
  const queijo = { id: "o4", grupoId: "g2", nome: "Queijo", precoCentavos: 400 };

  it("the unit price and total are (product + add-ons) x quantity, the server's sum", () => {
    const r = adicionarConfigurado([], { produto, quantidade: 3, opcoes: [bacon, queijo], observacao: null });
    expect(precoUnitario(r[0]!)).toBe(3900);
    expect(totalEmPreparo(r)).toBe(11_700);
  });

  it("the same combination merges (any order of add-ons), a different one is a new line, a plain tap stays on the plain line", () => {
    let r = adicionarConfigurado([], { produto, quantidade: 1, opcoes: [bacon, queijo], observacao: " sem cebola " });
    r = adicionarConfigurado(r, { produto, quantidade: 2, opcoes: [queijo, bacon], observacao: "sem cebola" });
    expect(r).toHaveLength(1);
    expect(r[0]?.quantidade).toBe(3);
    r = adicionarConfigurado(r, { produto, quantidade: 1, opcoes: [bacon], observacao: null });
    r = adicionar(r, produto);
    r = adicionar(r, produto);
    expect(r.map((i) => i.quantidade)).toEqual([3, 1, 2]);
    expect(definirQuantidade(r, r[1]!.chave, 0)).toHaveLength(2);
  });

  it("editing a line replaces it; if it now equals another line the two merge", () => {
    let r = adicionarConfigurado([], { produto, quantidade: 1, opcoes: [bacon], observacao: null });
    r = adicionar(r, produto);
    const comBacon = r[0]!;
    const editado = substituirItem(r, comBacon.chave, { produto, quantidade: 2, opcoes: [], observacao: null });
    expect(editado).toHaveLength(1);
    expect(editado[0]?.quantidade).toBe(3);
  });

  it("sends add-ons by id only, and the note as typed (trimmed); a plain item carries no opcaoIds", () => {
    const r = adicionarConfigurado([], { produto, quantidade: 2, opcoes: [bacon], observacao: "  sem gelo " });
    expect(paraItensDaRodada([...r, ...adicionar([], { ...produto, id: "p2" })])).toEqual([
      { produtoId: "p1", quantidade: 2, observacao: "sem gelo", opcaoIds: ["o3"] },
      { produtoId: "p2", quantidade: 1, observacao: null },
    ]);
    expect(normalizarObservacao("   ")).toBeNull();
    expect(normalizarObservacao("a\u0007b")).toBe("a b");
    expect(normalizarObservacao("x".repeat(300))).toHaveLength(200);
  });
});
