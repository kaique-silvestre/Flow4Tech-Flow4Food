import { act, renderHook } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { alternarTema, aplicarTemaSalvo, temaAtual, useTema } from "./tema";

function sistemaEscuro(escuro: boolean) {
  vi.stubGlobal(
    "matchMedia",
    vi.fn(() => ({ matches: escuro })),
  );
}

beforeEach(() => {
  window.localStorage.clear();
  delete document.documentElement.dataset["theme"];
});

afterEach(() => {
  vi.unstubAllGlobals();
});

describe("tema", () => {
  it("follows the phone until the person chooses", () => {
    sistemaEscuro(true);
    expect(temaAtual()).toBe("escuro");

    sistemaEscuro(false);
    expect(temaAtual()).toBe("claro");
  });

  it("puts the saved theme on the page before the first render", () => {
    sistemaEscuro(false);
    window.localStorage.setItem("f4t-tema", "escuro");

    aplicarTemaSalvo();

    expect(document.documentElement.dataset["theme"]).toBe("dark");
  });

  it("leaves the page alone when nothing was chosen, so the tokens follow the phone live", () => {
    sistemaEscuro(true);

    aplicarTemaSalvo();

    expect(document.documentElement.dataset["theme"]).toBeUndefined();
  });

  it("switches, remembers it on this device and tells the screens", () => {
    sistemaEscuro(false);
    aplicarTemaSalvo();
    const { result } = renderHook(() => useTema());
    expect(result.current.tema).toBe("claro");

    act(() => result.current.alternar());

    expect(result.current.tema).toBe("escuro");
    expect(document.documentElement.dataset["theme"]).toBe("dark");
    expect(window.localStorage.getItem("f4t-tema")).toBe("escuro");

    act(() => alternarTema());

    expect(result.current.tema).toBe("claro");
    expect(document.documentElement.dataset["theme"]).toBe("light");
  });

  it("ignores a stored value it does not know", () => {
    sistemaEscuro(false);
    window.localStorage.setItem("f4t-tema", "roxo");

    expect(temaAtual()).toBe("claro");
  });
});
