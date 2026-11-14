import { formatBRL } from "@flow4tech/design-system";
import { describe, expect, it } from "vitest";
import {
  diferencaTotal,
  esperadoDaGaveta,
  montarContagens,
  palavraDaDiferenca,
  problemaDoMovimento,
  type Resumo,
  textoDaDecisao,
  textoDoMovimento,
  tomDaDiferenca,
} from "./logica";

describe("difference in words", () => {
  it.each([
    [0, "Confere", "success"],
    [-500, `Falta ${formatBRL(500)}`, "alert"],
    [1_200, `Sobra ${formatBRL(1_200)}`, "warning"],
  ])("says %i as %s with tone %s", (valor, palavra, tom) => {
    expect(palavraDaDiferenca(valor)).toBe(palavra);
    expect(tomDaDiferenca(valor)).toBe(tom);
  });

  it("sums the absolute differences, so a shortage and a surplus never cancel", () => {
    expect(diferencaTotal(-300, [200, -100])).toBe(600);
    expect(diferencaTotal(0, [])).toBe(0);
  });
});

describe("blind count", () => {
  const resumo = (extra: Partial<Resumo>, oculto = false): Resumo =>
    ({ cego: false, esperadoCentavos: 12_750, turno: { esperadoOculto: oculto }, ...extra }) as Resumo;

  it("knows the expected value only when the server sent it and did not hide it", () => {
    expect(esperadoDaGaveta(resumo({}))).toBe(12_750);
    expect(esperadoDaGaveta(resumo({ cego: true }))).toBeNull();
    expect(esperadoDaGaveta(resumo({}, true))).toBeNull();
    expect(esperadoDaGaveta(resumo({ esperadoCentavos: null }))).toBeNull();
  });
});

describe("counts by method", () => {
  it("sends only the lines that were filled in, in cents", () => {
    expect(montarContagens({ a: "1500", b: "", c: "0" })).toEqual([
      { metodoId: "a", contadoCentavos: 1500 },
      { metodoId: "c", contadoCentavos: 0 },
    ]);
  });
});

describe("movement draft", () => {
  const base = { tipo: "sangria" as const, valorDigitos: "5000", motivo: "Levar ao cofre", destino: "Cofre" };

  it("is ready with value, reason and destination", () => {
    expect(problemaDoMovimento(base)).toBeNull();
  });

  it("asks for the value first, then the reason, then the destination of a withdrawal", () => {
    expect(problemaDoMovimento({ ...base, valorDigitos: "" })).toBe("valor");
    expect(problemaDoMovimento({ ...base, valorDigitos: "0" })).toBe("valor");
    expect(problemaDoMovimento({ ...base, motivo: "  " })).toBe("motivo");
    expect(problemaDoMovimento({ ...base, destino: " " })).toBe("destino");
  });

  it("does not need a destination for a supply but keeps its limits", () => {
    expect(problemaDoMovimento({ ...base, tipo: "suprimento", destino: "" })).toBeNull();
    expect(problemaDoMovimento({ ...base, motivo: "x".repeat(201) })).toBe("motivo");
    expect(problemaDoMovimento({ ...base, destino: "x".repeat(81) })).toBe("destino");
  });
});

describe("sentences of a refusal", () => {
  it("never says why a 403 happened", () => {
    expect(textoDoMovimento("sangria", 403)).toBe("Você não tem permissão para registrar a sangria com este valor.");
    expect(textoDaDecisao("aprovar", 403)).toBe("Você não tem permissão para decidir esta diferença.");
  });

  it("explains the cases the person can act on", () => {
    expect(textoDoMovimento("sangria", 409, "saldo-insuficiente")).toMatch(/gaveta não tem esse valor/);
    expect(textoDoMovimento("suprimento", 404)).toMatch(/já foi fechado/);
    expect(textoDaDecisao("aprovar", 409, "autoaprovacao-nao-permitida")).toMatch(/Quem fechou/);
    expect(textoDaDecisao("recontar", 409, "nao-pendente")).toMatch(/outra pessoa/);
  });
});
