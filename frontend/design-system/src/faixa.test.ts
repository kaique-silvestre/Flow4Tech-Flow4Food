import { act, renderHook } from "@testing-library/react";
import { afterEach, describe, expect, it } from "vitest";
import {
  CORTE_COMPUTADOR,
  CORTE_COMPUTADOR_PLENO,
  CORTE_TABLET,
  faixaDaLargura,
  menuRecolhido,
  useFaixa,
} from "./faixa";

function larguraDaJanela(px: number) {
  Object.defineProperty(window, "innerWidth", { configurable: true, writable: true, value: px });
}

afterEach(() => larguraDaJanela(1024));

describe("faixaDaLargura (ADR-019: 500 and 1000 are the only cuts)", () => {
  it("keeps the cuts as the ADR names them", () => {
    expect([CORTE_TABLET, CORTE_COMPUTADOR, CORTE_COMPUTADOR_PLENO]).toEqual([500, 1000, 1280]);
  });

  it.each([
    [320, "celular"],
    [390, "celular"],
    [499, "celular"],
    [499.98, "celular"],
    [500, "tablet"],
    [820, "tablet"],
    [999, "tablet"],
    [1000, "computador"],
    [1280, "computador"],
    [1440, "computador"],
  ] as const)("%s px is %s", (largura, faixa) => {
    expect(faixaDaLargura(largura)).toBe(faixa);
  });
});

describe("menuRecolhido (the person's own choice beats the automatic one)", () => {
  it("is the rail on a tablet and the full menu on a computer when nobody chose", () => {
    expect(menuRecolhido("tablet", null)).toBe(true);
    expect(menuRecolhido("computador", null)).toBe(false);
  });

  it("obeys a saved choice on both bands", () => {
    expect(menuRecolhido("computador", "trilho")).toBe(true);
    expect(menuRecolhido("tablet", "completo")).toBe(false);
  });

  it("has no side menu on the phone, whatever was saved", () => {
    expect(menuRecolhido("celular", "completo")).toBe(false);
  });
});

describe("useFaixa", () => {
  it("reads the window and follows a resize", () => {
    larguraDaJanela(390);
    const { result } = renderHook(() => useFaixa());
    expect(result.current).toBe("celular");

    act(() => {
      larguraDaJanela(820);
      window.dispatchEvent(new Event("resize"));
    });
    expect(result.current).toBe("tablet");

    act(() => {
      larguraDaJanela(1440);
      window.dispatchEvent(new Event("resize"));
    });
    expect(result.current).toBe("computador");
  });
});
