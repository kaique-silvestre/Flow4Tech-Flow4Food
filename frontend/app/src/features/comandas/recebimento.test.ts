import { describe, expect, it } from "vitest";
import {
  alternarMetodo,
  alvoDoRecebimento,
  apagarValor,
  calcularRecebimento,
  digitarValor,
  type LinhaDeRecebimento,
  type MetodoDeRecebimento,
  partesParaReceber,
  textoDoProblema,
  textoDoRecebimento,
} from "./recebimento";

const DINHEIRO: MetodoDeRecebimento = { id: "m-din", nome: "Dinheiro", dinheiro: true };
const PIX: MetodoDeRecebimento = { id: "m-pix", nome: "Pix", dinheiro: false };
const CREDITO: MetodoDeRecebimento = { id: "m-cre", nome: "Crédito", dinheiro: false };
const METODOS = [DINHEIRO, PIX, CREDITO];
const linha = (metodo: MetodoDeRecebimento, digitos = ""): LinhaDeRecebimento => ({
  pagamentoId: `p-${metodo.id}`,
  metodoId: metodo.id,
  digitos,
});

describe("the balance of a receipt", () => {
  it("gives the whole balance to a single empty line", () => {
    const c = calcularRecebimento([linha(PIX)], METODOS, 2750);

    expect(c.valores).toEqual([2750]);
    expect(c.aplicadoCentavos).toBe(2750);
    expect(c.faltaCentavos).toBe(0);
    expect(c.trocoCentavos).toBe(0);
    expect(c.problema).toBeNull();
  });

  it("gives change only for cash: R$ 30,00 on R$ 27,50 is R$ 2,50", () => {
    const c = calcularRecebimento([linha(DINHEIRO, "3000")], METODOS, 2750);

    expect(c.trocoCentavos).toBe(250);
    expect(c.aplicadoCentavos).toBe(2750);
    expect(c.faltaCentavos).toBe(0);
  });

  it("a partial payment leaves what remains", () => {
    const c = calcularRecebimento([linha(DINHEIRO, "1000")], METODOS, 2750);

    expect(c.faltaCentavos).toBe(1750);
    expect(c.trocoCentavos).toBe(0);
  });

  it("mixes two methods: the line left empty takes the rest", () => {
    const c = calcularRecebimento([linha(PIX), linha(DINHEIRO, "1000")], METODOS, 2750);

    expect(c.valores).toEqual([1750, 1000]);
    expect(c.faltaCentavos).toBe(0);
    expect(c.problema).toBeNull();
  });

  it("cash over what the other methods left is change", () => {
    const c = calcularRecebimento([linha(PIX, "1000"), linha(DINHEIRO, "2000")], METODOS, 2750);

    expect(c.dinheiroAplicadoCentavos).toBe(1750);
    expect(c.trocoCentavos).toBe(250);
    expect(c.faltaCentavos).toBe(0);
  });

  it("refuses methods that are not cash passing the balance (no change for them)", () => {
    const c = calcularRecebimento([linha(PIX, "2000"), linha(CREDITO, "1000")], METODOS, 2750);

    expect(c.problema).toEqual({ tipo: "excede" });
    expect(textoDoProblema(c.problema!, METODOS)).toMatch(/passam do que falta/);
  });

  it("refuses a line without value and a cash line with nothing left to pay", () => {
    expect(calcularRecebimento([linha(PIX, "0")], METODOS, 2750).problema).toEqual({
      tipo: "sem-valor",
      metodoId: "m-pix",
    });
    const dinheiroSobrando = calcularRecebimento([linha(PIX, "2750"), linha(DINHEIRO, "500")], METODOS, 2750);
    expect(dinheiroSobrando.problema).toEqual({ tipo: "dinheiro-sem-saldo", metodoId: "m-din" });
  });

  it("aims at a target smaller than the bill (a part of the split)", () => {
    const c = calcularRecebimento([linha(PIX)], METODOS, 1375);

    expect(c.valores).toEqual([1375]);
    expect(c.faltaCentavos).toBe(0);
  });
});

describe("toggling methods", () => {
  it("adds a line and makes it the active one when several lines are allowed", () => {
    const a = alternarMetodo([], PIX.id, true, "p1");
    const b = alternarMetodo(a.linhas, DINHEIRO.id, true, "p2");

    expect(b.linhas.map((l) => l.metodoId)).toEqual([PIX.id, DINHEIRO.id]);
    expect(b.ativo).toBe(DINHEIRO.id);
  });

  it("replaces the only line when several are not allowed", () => {
    const a = alternarMetodo([], PIX.id, false, "p1");
    const b = alternarMetodo(a.linhas, DINHEIRO.id, false, "p2");

    expect(b.linhas.map((l) => l.metodoId)).toEqual([DINHEIRO.id]);
  });

  it("takes a method out when it is toggled again", () => {
    const a = alternarMetodo(alternarMetodo([], PIX.id, true, "p1").linhas, DINHEIRO.id, true, "p2");
    const b = alternarMetodo(a.linhas, DINHEIRO.id, true, "p3");

    expect(b.linhas.map((l) => l.metodoId)).toEqual([PIX.id]);
    expect(b.ativo).toBe(PIX.id);
    expect(alternarMetodo(b.linhas, PIX.id, true, "p4")).toEqual({ linhas: [], ativo: null });
  });

  it("keeps the payment id of the lines that stay", () => {
    const a = alternarMetodo(alternarMetodo([], PIX.id, true, "p1").linhas, DINHEIRO.id, true, "p2");

    expect(a.linhas.map((l) => l.pagamentoId)).toEqual(["p1", "p2"]);
  });
});

describe("typing an amount", () => {
  it("fills the cents from the right and drops leading zeros", () => {
    expect(digitarValor(digitarValor(digitarValor("", "7"), "5"), "0")).toBe("750");
    expect(digitarValor("", "0")).toBe("0");
    expect(digitarValor("0", "5")).toBe("5");
  });

  it("stops at nine digits and erases one at a time", () => {
    expect(digitarValor("123456789", "1")).toBe("123456789");
    expect(apagarValor("750")).toBe("75");
    expect(apagarValor("")).toBe("");
  });
});

describe("the parts of a split", () => {
  const partes = [
    { numero: 1, assento: null, totalCentavos: 1375 },
    { numero: 2, assento: 3, totalCentavos: 1375 },
  ];

  it("takes from each part what was already applied to it", () => {
    const r = partesParaReceber("d1", partes, [
      { divisaoId: "d1", divisaoParte: 1, aplicadoCentavos: 1000, estado: "confirmado" },
      { divisaoId: "d1", divisaoParte: 1, aplicadoCentavos: 375, estado: "confirmado" },
      { divisaoId: "d1", divisaoParte: 2, aplicadoCentavos: 999, estado: "pendente" },
      { divisaoId: "outra", divisaoParte: 2, aplicadoCentavos: 500, estado: "confirmado" },
      { divisaoId: null, divisaoParte: null, aplicadoCentavos: 500, estado: "confirmado" },
    ]);

    expect(r.map((p) => p.faltaCentavos)).toEqual([0, 1375]);
    expect(r[1]?.assento).toBe(3);
  });

  it("owes the whole part when the payments are not known", () => {
    expect(partesParaReceber("d1", partes, null).map((p) => p.faltaCentavos)).toEqual([1375, 1375]);
  });

  it("aims at the part, never past the balance of the bill", () => {
    const parte = { numero: 1, assento: null, totalCentavos: 1375, faltaCentavos: 1375 };

    expect(alvoDoRecebimento(2750, null)).toBe(2750);
    expect(alvoDoRecebimento(2750, parte)).toBe(1375);
    expect(alvoDoRecebimento(1000, parte)).toBe(1000);
  });
});

describe("what the server refused", () => {
  it("never reveals why a 403 was refused", () => {
    expect(textoDoRecebimento(403, undefined)).toBe("Você não tem permissão para receber desta forma.");
  });

  it("says what to do on each conflict", () => {
    expect(textoDoRecebimento(409, "versao-desatualizada")).toMatch(/outro aparelho/);
    expect(textoDoRecebimento(409, "excede-parte")).toMatch(/nesta parte/);
    expect(textoDoRecebimento(409, "divisao-invalida")).toMatch(/dividida/);
    expect(textoDoRecebimento(409, "metodo-inativo")).toMatch(/desativado/);
    expect(textoDoRecebimento(500, undefined)).toMatch(/Não foi possível/);
  });
});
