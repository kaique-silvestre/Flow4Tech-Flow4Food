import { describe, expect, it } from "vitest";
import { esperaDoBackoff, lerEventos, precisaRecarregar } from "./fluxo";

describe("SSE reading", () => {
  it("reads whole events and keeps the partial one for the next chunk", () => {
    const a = lerEventos("", ': aberto\n\nevent: resync\ndata: {"motivo":"abertura","comandas":[]}\n\nevent: com');
    expect(a.eventos).toEqual([{ nome: "resync", id: null, dados: { motivo: "abertura", comandas: [] } }]);

    const b = lerEventos(a.resto, 'anda\nid: e1\ndata: {"comandas":[{"comandaId":"c1","versao":3}]}\n\n');
    expect(b.eventos).toEqual([{ nome: "comanda", id: "e1", dados: { comandas: [{ comandaId: "c1", versao: 3 }] } }]);
    expect(b.resto).toBe("");
  });

  it("ignores comments (keep-alive) and data that is not JSON", () => {
    expect(lerEventos("", ": batimento\n\n").eventos).toEqual([]);
    expect(lerEventos("", "event: comanda\ndata: {quebrado\n\n").eventos).toEqual([]);
  });

  it("accepts CRLF line ends", () => {
    expect(lerEventos("", 'event: resync\r\ndata: {"comandas":[]}\r\n\r\n').eventos).toHaveLength(1);
  });
});

describe("version order rule", () => {
  const tela = new Map([
    ["c1", 3],
    ["c2", 5],
  ]);

  it("reloads only when an event carries a version higher than the one on screen", () => {
    expect(precisaRecarregar([{ comandaId: "c1", versao: 4 }], tela)).toBe(true);
    expect(precisaRecarregar([{ comandaId: "c1", versao: 3 }], tela)).toBe(false);
    expect(precisaRecarregar([{ comandaId: "c2", versao: 2 }], tela)).toBe(false);
  });

  it("reloads for a comanda the screen does not hold yet", () => {
    expect(precisaRecarregar([{ comandaId: "c9", versao: 1 }], tela)).toBe(true);
  });
});

describe("backoff", () => {
  it("doubles from one second and stops at fifteen", () => {
    expect([1, 2, 3, 4, 5, 6, 20].map(esperaDoBackoff)).toEqual([1000, 2000, 4000, 8000, 15000, 15000, 15000]);
  });
});
