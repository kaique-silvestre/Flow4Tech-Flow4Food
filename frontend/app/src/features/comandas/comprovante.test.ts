import { afterEach, describe, expect, it, vi } from "vitest";
import { classificarFalha, compartilharTexto, horaCurta, paraComprovante, rotuloDoItem } from "./comprovante";

afterEach(() => {
  vi.unstubAllGlobals();
});

const dto = {
  tipo: "pre-conta",
  segundaVia: false,
  aviso: "Sem valor fiscal",
  numero: 7,
  numeroTexto: "#7",
  identificacao: "Ana",
  diaOperacional: "2026-11-14",
  abertaEm: "2026-11-14T20:00:00Z",
  fechadaEm: null,
  contaPedidaEm: null,
  geradaEm: "2026-11-14T21:00:00Z",
  itens: [
    {
      descricao: "Chopp",
      quantidade: 2,
      unitarioCentavos: "1250",
      totalCentavos: 2500,
      tipo: "normal",
      observacao: null,
      opcoes: ["Gelada"],
    },
  ],
  descontos: [],
  conta: {
    consumoCentavos: 2500,
    descontoCentavos: 0,
    taxaServicoCentavos: 250,
    taxaServicoRemovida: false,
    taxaServicoPercentual: 10,
    totalCentavos: 2750,
    pagoCentavos: 0,
    saldoCentavos: 2750,
  },
  pagamentos: [],
  partes: null,
};

describe("paraComprovante", () => {
  it("turns the server's numbers (number or string) into numbers and keeps the optional parts empty", () => {
    const c = paraComprovante(dto as never);
    expect(c.itens[0]?.unitarioCentavos).toBe(1250);
    expect(c.conta.totalCentavos).toBe(2750);
    expect(c.partes).toBeNull();
    expect(c.contaPedidaEm).toBeNull();
  });
});

describe("rotuloDoItem", () => {
  it("marks courtesy and gift items and leaves the normal ones alone", () => {
    expect(rotuloDoItem("cortesia")).toBe("Cortesia");
    expect(rotuloDoItem("brinde")).toBe("Brinde");
    expect(rotuloDoItem("normal")).toBeNull();
  });
});

describe("horaCurta", () => {
  it("is empty for an invalid date", () => {
    expect(horaCurta("x")).toBe("");
  });
});

describe("classificarFalha", () => {
  it("names each refusal the screen words differently", () => {
    expect(classificarFalha(403)).toBe("proibido");
    expect(classificarFalha(404)).toBe("nao-encontrada");
    expect(classificarFalha(409)).toBe("estado");
    expect(classificarFalha(500)).toBe("outro");
    expect(classificarFalha(null)).toBe("rede");
  });
});

describe("compartilharTexto", () => {
  it("uses the share sheet when the phone has one", async () => {
    const share = vi.fn().mockResolvedValue(undefined);
    vi.stubGlobal("navigator", { share });
    expect(await compartilharTexto("Pré-conta", "texto")).toBe("compartilhado");
    expect(share).toHaveBeenCalledWith({ title: "Pré-conta", text: "texto" });
  });

  it("treats a dismissed share sheet as nothing happened", async () => {
    vi.stubGlobal("navigator", { share: vi.fn().mockRejectedValue(new DOMException("x", "AbortError")) });
    expect(await compartilharTexto("Pré-conta", "texto")).toBe("cancelado");
  });

  it("copies the text when there is no share sheet", async () => {
    const writeText = vi.fn().mockResolvedValue(undefined);
    vi.stubGlobal("navigator", { clipboard: { writeText } });
    expect(await compartilharTexto("Pré-conta", "texto")).toBe("copiado");
    expect(writeText).toHaveBeenCalledWith("texto");
  });

  it("copies when the share sheet fails for another reason", async () => {
    const writeText = vi.fn().mockResolvedValue(undefined);
    vi.stubGlobal("navigator", { share: vi.fn().mockRejectedValue(new Error("boom")), clipboard: { writeText } });
    expect(await compartilharTexto("Pré-conta", "texto")).toBe("copiado");
  });

  it("says so when nothing works", async () => {
    vi.stubGlobal("navigator", {});
    expect(await compartilharTexto("Pré-conta", "texto")).toBe("indisponivel");
  });
});
