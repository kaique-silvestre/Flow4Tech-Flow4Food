import { describe, expect, it } from "vitest";
import { digitarPercentual, numeroValido, percentualNaTela, previaDoTotal, quantoTira, textoDaFalha } from "./desconto";
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

  it("takes a decimal percent, half a cent going up, with no float error", () => {
    expect(quantoTira("percentual", 7.5, 10_000)).toBe(750);
    expect(quantoTira("percentual", 7.5, 1_010)).toBe(76); // 75,75
    expect(quantoTira("percentual", 0.5, 100)).toBe(1); // 0,5 exact: up
    expect(quantoTira("percentual", 0.5, 99)).toBe(0); // 0,495: down
    expect(quantoTira("percentual", 0.01, 5_000)).toBe(1); // 0,5: up
    expect(quantoTira("percentual", 0.01, 4_999)).toBe(0);
    expect(quantoTira("percentual", 33.33, 1)).toBe(0);
    expect(quantoTira("percentual", 33.33, 3)).toBe(1); // 0,9999
    expect(quantoTira("percentual", 0.07, 50_000)).toBe(35); // 0.07*100 is 7.000000000000001 in float
    expect(quantoTira("percentual", 1.15, 1_000)).toBe(12); // 11,5 up
    expect(quantoTira("percentual", 100, 10_001)).toBe(10_001);
  });

  it("never takes more than the base", () => {
    expect(quantoTira("valor", 50_000, 10_000)).toBe(10_000);
    expect(quantoTira("percentual", 100, 10_000)).toBe(10_000);
    expect(quantoTira("valor", 100, -5)).toBe(0);
  });
});

describe("numeroValido", () => {
  it("accepts percents above 0 up to 100 with two decimals, and any value of at least one cent", () => {
    expect(numeroValido("percentual", 0)).toBe(false);
    expect(numeroValido("percentual", 0.01)).toBe(true);
    expect(numeroValido("percentual", 7.5)).toBe(true);
    expect(numeroValido("percentual", 0.07)).toBe(true);
    expect(numeroValido("percentual", 100.01)).toBe(false);
    expect(numeroValido("percentual", 7.555)).toBe(false);
    expect(numeroValido("percentual", -1)).toBe(false);
    expect(numeroValido("percentual", 101)).toBe(false);
    expect(numeroValido("percentual", 100)).toBe(true);
    expect(numeroValido("valor", 0)).toBe(false);
    expect(numeroValido("valor", 1)).toBe(true);
  });
});

describe("digitarPercentual", () => {
  const digitar = (teclas: string) => [...teclas].reduce(digitarPercentual, "");

  it("types whole and decimal percents, at most two decimals and three whole digits", () => {
    expect(digitar("75,5")).toBe("75.5");
    expect(digitar("7,555")).toBe("7.55");
    expect(digitar("1000")).toBe("100");
    expect(digitar("12,3,4")).toBe("12.34");
  });

  it("starts the decimals with 0 when nothing was typed and drops leading zeros", () => {
    expect(digitar(",5")).toBe("0.5");
    expect(digitar("05")).toBe("5");
    expect(digitar("0,05")).toBe("0.05");
    expect(digitar("00")).toBe("");
  });

  it("shows the decimal comma", () => {
    expect(percentualNaTela("7.5")).toBe("7,5%");
    expect(percentualNaTela("")).toBe("0%");
    expect(percentualNaTela("10")).toBe("10%");
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

  it("previews a decimal percent in the server order: discount, then the fee on what is left, half up", () => {
    const tira = quantoTira("percentual", 7.5, 10_000); // 750
    expect(previaDoTotal(conta, tira)).toEqual({
      descontoCentavos: 750,
      taxaServicoCentavos: 925,
      totalCentavos: 10_175,
    });
    const odd = quantoTira("percentual", 12.34, 3_333); // 411,29 -> 411
    expect(previaDoTotal({ ...conta, consumoCentavos: 3_333 }, odd)).toEqual({
      descontoCentavos: 411,
      taxaServicoCentavos: 292, // 2922 * 10% = 292,2
      totalCentavos: 3_214,
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
