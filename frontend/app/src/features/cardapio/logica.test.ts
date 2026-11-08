import { describe, expect, it } from "vitest";
import {
  agrupar,
  corpoDaAlteracao,
  deInputLocal,
  filtrar,
  lerDataHoraBR,
  mascararDataHora,
  type ProdutoGestao,
  paraInputLocal,
  paraTextoBR,
  validarPreco,
} from "./logica";

const produto = (id: string, nome: string, extra: Partial<ProdutoGestao> = {}): ProdutoGestao => ({
  id,
  categoriaId: "bebidas",
  codigo: id.toUpperCase(),
  nome,
  precoCentavos: 1000,
  precoEfetivoCentavos: 1000,
  precoDaUnidadeCentavos: null,
  ativo: true,
  indisponivel: false,
  versao: 1,
  promocao: null,
  ...extra,
});

const categorias = [
  { id: "comidas", nome: "Comidas", ativa: true, categoriaPaiId: null },
  { id: "bebidas", nome: "Bebidas", ativa: true, categoriaPaiId: null },
  { id: "cervejas", nome: "Cervejas", ativa: true, categoriaPaiId: "bebidas" },
];

describe("agrupar", () => {
  it("lists parents by name with their subcategories right after, and each product under its own category", () => {
    const grupos = agrupar(
      [
        produto("a", "Água"),
        produto("c", "Chopp", { categoriaId: "cervejas" }),
        produto("p", "Pastel", { categoriaId: "comidas" }),
      ],
      categorias,
    );
    expect(grupos.map((g) => [g.nome, g.nivel, g.produtos.map((p) => p.nome)])).toEqual([
      ["Bebidas", 0, ["Água"]],
      ["Cervejas", 1, ["Chopp"]],
      ["Comidas", 0, ["Pastel"]],
    ]);
  });

  it("drops categories with no product and keeps a parent that only has children shown through them", () => {
    const grupos = agrupar([produto("c", "Chopp", { categoriaId: "cervejas" })], categorias);
    expect(grupos.map((g) => g.nome)).toEqual(["Bebidas", "Cervejas"]);
    expect(grupos[0]?.produtos).toEqual([]);
  });

  it("puts a product whose category is unknown under 'Sem categoria'", () => {
    const grupos = agrupar([produto("x", "Misterio", { categoriaId: "sumiu" })], categorias);
    expect(grupos.map((g) => g.nome)).toEqual(["Sem categoria"]);
  });
});

describe("filtrar", () => {
  it("matches name or code, ignoring case and accents", () => {
    const todos = [produto("a", "Água"), produto("c", "Chopp", { codigo: "CHOPP" })];
    expect(filtrar(todos, "agua").map((p) => p.id)).toEqual(["a"]);
    expect(filtrar(todos, " chop ").map((p) => p.id)).toEqual(["c"]);
    expect(filtrar(todos, "")).toHaveLength(2);
    expect(filtrar(todos, "zzz")).toEqual([]);
  });
});

describe("validarPreco", () => {
  const ok = { base: "1000", promo: "", inicio: "", fim: "" };

  it("accepts a plain price and refuses zero or empty", () => {
    expect(validarPreco(ok)).toEqual({});
    expect(validarPreco({ ...ok, base: "" }).base).toBeDefined();
    expect(validarPreco({ ...ok, base: "0" }).base).toBeDefined();
  });

  it("needs a promotional price below the base, with a window that ends after it starts", () => {
    const promo = { base: "1000", promo: "800", inicio: "2026-11-10T10:00", fim: "2026-11-10T22:00" };
    expect(validarPreco(promo)).toEqual({});
    expect(validarPreco({ ...promo, promo: "1000" }).promo).toBeDefined();
    expect(validarPreco({ ...promo, inicio: "" }).inicio).toBeDefined();
    expect(validarPreco({ ...promo, fim: "" }).fim).toBeDefined();
    expect(validarPreco({ ...promo, fim: "2026-11-10T09:00" }).fim).toBeDefined();
  });

  it("ignores window fields when there is no promotional price", () => {
    expect(validarPreco({ base: "1000", promo: "", inicio: "2026-11-10T10:00", fim: "" })).toEqual({});
  });
});

describe("local date inputs", () => {
  it("round-trips an instant through the datetime-local format", () => {
    const iso = "2026-11-10T13:00:00.000Z";
    expect(deInputLocal(paraInputLocal(iso))).toBe(iso);
  });
});

describe("corpoDaAlteracao", () => {
  it("sends the whole product with the version the screen saw, changing only what was asked", () => {
    const p = produto("a", "Água", { versao: 7, precoCentavos: 500 });
    expect(corpoDaAlteracao(p, { indisponivel: true })).toEqual({
      codigo: "A",
      nome: "Água",
      precoCentavos: 500,
      categoriaId: "bebidas",
      ativo: true,
      indisponivel: true,
      versao: 7,
      promocao: null,
    });
  });

  it("keeps the promotion unless a new one (or null) is given", () => {
    const promocao = { precoCentavos: 400, inicio: "2026-11-10T10:00:00Z", fim: "2026-11-10T22:00:00Z" };
    const p = produto("a", "Água", { promocao });
    expect(corpoDaAlteracao(p, { ativo: false }).promocao).toEqual(promocao);
    expect(corpoDaAlteracao(p, { promocao: null }).promocao).toBeNull();
  });
});

describe("date and time typed as dd/mm/aaaa hh:mm", () => {
  it("lays the separators in while digits are typed", () => {
    expect(mascararDataHora("1")).toBe("1");
    expect(mascararDataHora("101")).toBe("10/1");
    expect(mascararDataHora("10112026")).toBe("10/11/2026");
    expect(mascararDataHora("101120261030")).toBe("10/11/2026 10:30");
    expect(mascararDataHora("10/11/2026 10:30 99")).toBe("10/11/2026 10:30");
  });

  it("reads a real date and time, and refuses the rest", () => {
    expect(lerDataHoraBR("10/11/2026 18:05")).toBe("2026-11-10T18:05");
    expect(lerDataHoraBR("")).toBe("");
    expect(lerDataHoraBR("31/02/2026 10:00")).toBeNull();
    expect(lerDataHoraBR("10/11/2026 25:00")).toBeNull();
    expect(lerDataHoraBR("10/11/26 10:00")).toBeNull();
  });

  it("shows a stored value back in the same format", () => {
    expect(paraTextoBR("2026-11-10T18:05")).toBe("10/11/2026 18:05");
    expect(lerDataHoraBR(paraTextoBR("2026-11-10T18:05"))).toBe("2026-11-10T18:05");
  });
});
