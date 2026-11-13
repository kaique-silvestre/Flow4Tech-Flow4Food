import { describe, expect, it } from "vitest";
import { numeroValido, previaDoTotal, quantoTira, textoDaFalha } from "./desconto";
import type { ContaDaComanda } from "./rodada";

const conta: ContaDaComanda = {
  consumoCentavos: 10_000,
  taxaServicoCentavos: 1000,
  totalCentavos: 11_000,
  pagoCentavos: 0,
  saldoCentavos: 11_000,
  taxaServicoPercentual: 10,
  taxaServicoRemovida: false,
  descontoCentavos: 0,
  versao: 3,
  estado: "aberta",
};

describe("quantoTira", () => {
  it("takes the percent of the base, half a cent going up", () => {
    expect(quantoTira("percentual", 10, 10_000)).toBe(1000);
    expect(quantoTira("percentual", 15, 1_010)).toBe(152);
  });

  it("never takes more than the base", () => {
    expect(quantoTira("valor", 50_000, 10_000)).toBe(10_000);
    expect(quantoTira("percentual", 100, 10_000)).toBe(10_000);
    expect(quantoTira("valor", 100, -5)).toBe(0);
  });
});

describe("numeroValido", () => {
  it("accepts whole percents from 1 to 100 and any value of at least one cent", () => {
    expect(numeroValido("percentual", 0)).toBe(false);
    expect(numeroValido("percentual", 101)).toBe(false);
    expect(numeroValido("percentual", 100)).toBe(true);
    expect(numeroValido("valor", 0)).toBe(false);
    expect(numeroValido("valor", 1)).toBe(true);
  });
});

describe("previaDoTotal", () => {
  it("charges the service fee on what is left after the discount", () => {
    expect(previaDoTotal(conta, 2_000)).toEqual({
      descontoCentavos: 2_000,
      taxaServicoCentavos: 800,
      totalCentavos: 8_800,
    });
  });

  it("adds to the discount already given and never goes below zero", () => {
    expect(previaDoTotal({ ...conta, descontoCentavos: 4_000 }, 9_000).totalCentavos).toBe(0);
  });

  it("has no fee when the fee was removed", () => {
    expect(previaDoTotal({ ...conta, taxaServicoRemovida: true }, 1_000).totalCentavos).toBe(9_000);
  });
});

describe("textoDaFalha", () => {
  it("says a 403 is a value without permission and never names a limit", () => {
    const texto = textoDaFalha(403, undefined);

    expect(texto).toBe("Sem permissão para este valor.");
    expect(texto).not.toMatch(/\d/);
  });

  it("has its own sentence for the 409 answers", () => {
    expect(textoDaFalha(409, "desconto-excede")).toMatch(/falta cobrar/);
    expect(textoDaFalha(409, "versao-desatualizada")).toMatch(/outro aparelho/);
    expect(textoDaFalha(500, undefined)).toMatch(/Tente de novo/);
  });
});
