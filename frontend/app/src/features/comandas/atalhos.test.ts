import { describe, expect, it } from "vitest";
import { atalhoDaTecla, lancamentoRapido } from "./atalhos";

/** Shortcuts of the comandas screen. Reference: .scratch/S-00-novo-sistema/prototipos/index.html#m=web (keydown, l. 7350-7374). */
const tecla = (key: string, extra: Partial<{ ctrlKey: boolean; metaKey: boolean; altKey: boolean }> = {}) => ({
  key,
  ctrlKey: false,
  metaKey: false,
  altKey: false,
  ...extra,
});
const livre = { faixa: "computador", emCampo: false, dialogoAberto: false, painelAberto: true } as const;

describe("atalhoDaTecla", () => {
  it("maps each key to its action on the computer", () => {
    expect(atalhoDaTecla(tecla("n"), livre)).toBe("nova");
    expect(atalhoDaTecla(tecla("N"), livre)).toBe("nova");
    expect(atalhoDaTecla(tecla("["), livre)).toBe("menu");
    expect(atalhoDaTecla(tecla("k", { ctrlKey: true }), livre)).toBe("buscar");
    expect(atalhoDaTecla(tecla("k", { metaKey: true }), livre)).toBe("buscar");
    expect(atalhoDaTecla(tecla("Escape"), livre)).toBe("fechar");
  });

  it("does nothing on the phone and on the tablet (no reliable way to know a physical keyboard)", () => {
    for (const faixa of ["celular", "tablet"] as const) {
      for (const t of [tecla("n"), tecla("["), tecla("k", { ctrlKey: true }), tecla("Escape")]) {
        expect(atalhoDaTecla(t, { ...livre, faixa })).toBeNull();
      }
    }
  });

  it("keeps single keys out of a field and out of a dialog, but Ctrl+K still searches from a field", () => {
    expect(atalhoDaTecla(tecla("n"), { ...livre, emCampo: true })).toBeNull();
    expect(atalhoDaTecla(tecla("["), { ...livre, emCampo: true })).toBeNull();
    expect(atalhoDaTecla(tecla("Escape"), { ...livre, emCampo: true })).toBeNull();
    expect(atalhoDaTecla(tecla("k", { ctrlKey: true }), { ...livre, emCampo: true })).toBe("buscar");
    expect(atalhoDaTecla(tecla("k", { ctrlKey: true }), { ...livre, dialogoAberto: true })).toBeNull();
    expect(atalhoDaTecla(tecla("n"), { ...livre, dialogoAberto: true })).toBeNull();
    expect(atalhoDaTecla(tecla("Escape"), { ...livre, dialogoAberto: true })).toBeNull();
  });

  it("ignores single keys with a modifier held, and Esc with no panel open", () => {
    expect(atalhoDaTecla(tecla("n", { ctrlKey: true }), livre)).toBeNull();
    expect(atalhoDaTecla(tecla("n", { metaKey: true }), livre)).toBeNull();
    expect(atalhoDaTecla(tecla("[", { altKey: true }), livre)).toBeNull();
    expect(atalhoDaTecla(tecla("Escape"), { ...livre, painelAberto: false })).toBeNull();
    expect(atalhoDaTecla(tecla("x"), livre)).toBeNull();
  });
});

describe("lancamentoRapido", () => {
  it("reads quantity and term", () => {
    expect(lancamentoRapido("3 chope")).toEqual({ quantidade: 3, termo: "chope" });
    expect(lancamentoRapido("  12   Porção de fritas ")).toEqual({ quantidade: 12, termo: "Porção de fritas" });
  });

  it("is not a quick launch without a quantity, a term, or with an absurd quantity", () => {
    expect(lancamentoRapido("chope")).toBeNull();
    expect(lancamentoRapido("3")).toBeNull();
    expect(lancamentoRapido("3 ")).toBeNull();
    expect(lancamentoRapido("0 chope")).toBeNull();
    expect(lancamentoRapido("1000 chope")).toBeNull();
  });
});
