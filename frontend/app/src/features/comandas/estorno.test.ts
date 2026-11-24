import { describe, expect, it } from "vitest";
import { corpoDoEstorno, estornavel, motivoFinal, textoDoEstorno, valorDoEstorno, valorValido } from "./estorno";

describe("estorno rules", () => {
  it("only a confirmed payment can be refunded", () => {
    expect(estornavel({ estado: "confirmado" })).toBe(true);
    expect(estornavel({ estado: "pendente" })).toBe(false);
  });

  it("total takes the whole refundable balance; partial takes the typed cents", () => {
    expect(valorDoEstorno("total", "", 3000)).toBe(3000);
    expect(valorDoEstorno("parcial", "1250", 3000)).toBe(1250);
    expect(valorDoEstorno("parcial", "", 3000)).toBe(0);
  });

  it("a partial value is between one cent and the balance; nothing refundable is never valid", () => {
    expect(valorValido("parcial", "", 3000)).toBe(false);
    expect(valorValido("parcial", "1", 3000)).toBe(true);
    expect(valorValido("parcial", "3000", 3000)).toBe(true);
    expect(valorValido("parcial", "3001", 3000)).toBe(false);
    expect(valorValido("total", "", 0)).toBe(false);
    expect(valorValido("total", "", 3000)).toBe(true);
  });

  it("the reason is the chip, or the trimmed text for Outro, never over 200 characters", () => {
    expect(motivoFinal("Erro de digitação", "")).toBe("Erro de digitação");
    expect(motivoFinal("Outro", "  ")).toBe("");
    expect(motivoFinal("Outro", "  troco errado ")).toBe("troco errado");
    expect(motivoFinal("Outro", "x".repeat(300))).toHaveLength(200);
    expect(motivoFinal(null, "")).toBe("");
  });

  it("the body carries no value for a total and never a limit or a shift", () => {
    expect(corpoDoEstorno("total", "", "m")).toEqual({ valorCentavos: null, motivo: "m" });
    expect(corpoDoEstorno("parcial", "500", "m")).toEqual({ valorCentavos: 500, motivo: "m" });
  });

  it("a 403 never says why", () => {
    expect(textoDoEstorno(403, "negado")).toBe("Você não tem permissão para estornar este valor.");
    expect(textoDoEstorno(409, "caixa-sem-dinheiro")).toMatch(/dinheiro suficiente/);
    expect(textoDoEstorno(409, "sem-turno-aberto")).toMatch(/turno/);
    expect(textoDoEstorno(400, "excede-saldo-estornavel")).toMatch(/passa do que/);
  });
});
