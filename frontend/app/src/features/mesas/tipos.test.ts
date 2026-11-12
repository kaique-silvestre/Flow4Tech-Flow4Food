import { describe, expect, it } from "vitest";
import {
  detalheDaMesa,
  type MesaDoMapa,
  nomeDaMesa,
  paraMapa,
  resultadoDaTroca,
  semMesas,
  statusDaMesa,
  todasAsMesas,
} from "./tipos";

const mesa = (extra: Partial<MesaDoMapa> = {}): MesaDoMapa => ({
  id: "m1",
  identificacao: "1",
  capacidade: null,
  areaId: null,
  status: "livre",
  comandasAbertas: 0,
  abertaDesde: null,
  ...extra,
});

describe("map parsing", () => {
  it("reads areas, tables and the derived fields", () => {
    const mapa = paraMapa({
      areas: [
        {
          id: "a1",
          nome: "Varanda",
          mesas: [
            {
              id: "m3",
              identificacao: "V1",
              capacidade: 4,
              areaId: "a1",
              status: "ocupada",
              comandasAbertas: 2,
              abertaDesde: "2026-11-11T20:00:00Z",
            },
          ],
        },
      ],
      semArea: [
        {
          id: "m9",
          identificacao: "Balcão",
          capacidade: null,
          areaId: null,
          status: "livre",
          comandasAbertas: 0,
          abertaDesde: null,
        },
      ],
    });

    expect(mapa.areas).toEqual([
      {
        id: "a1",
        nome: "Varanda",
        mesas: [
          mesa({
            id: "m3",
            identificacao: "V1",
            capacidade: 4,
            areaId: "a1",
            status: "ocupada",
            comandasAbertas: 2,
            abertaDesde: "2026-11-11T20:00:00Z",
          }),
        ],
      },
    ]);
    expect(mapa.semArea.map((m) => m.identificacao)).toEqual(["Balcão"]);
    expect(todasAsMesas(mapa).map((m) => m.id)).toEqual(["m3", "m9"]);
  });

  it("treats a missing or unknown status as free and a missing list as empty", () => {
    expect(statusDaMesa("qualquer")).toBe("livre");
    expect(statusDaMesa(undefined)).toBe("livre");
    expect(statusDaMesa("aguardando-conta")).toBe("aguardando-conta");
    expect(paraMapa({})).toEqual({ areas: [], semArea: [] });
  });

  it("says there is nothing to show for an empty map, even with empty areas", () => {
    expect(semMesas(paraMapa({}))).toBe(true);
    expect(semMesas(paraMapa({ areas: [{ id: "a", nome: "Vazia", mesas: [] }] }))).toBe(true);
    expect(semMesas(paraMapa({ semArea: [{ id: "m", identificacao: "1" }] }))).toBe(false);
  });
});

describe("table row text", () => {
  const agora = Date.parse("2026-11-11T20:12:00Z");

  it("shows the seats of a free table and nothing when unknown", () => {
    expect(detalheDaMesa(mesa({ capacidade: 4 }), agora)).toBe("4 lugares");
    expect(detalheDaMesa(mesa({ capacidade: 1 }), agora)).toBe("1 lugar");
    expect(detalheDaMesa(mesa(), agora)).toBe("");
  });

  it("shows the elapsed time since the first comanda opened, and the count only when there is more than one", () => {
    const aberta = { status: "ocupada" as const, abertaDesde: "2026-11-11T20:00:00Z" };
    expect(detalheDaMesa(mesa({ ...aberta, comandasAbertas: 1 }), agora)).toBe("há 12 min");
    expect(detalheDaMesa(mesa({ ...aberta, comandasAbertas: 3 }), agora)).toBe("3 comandas · há 12 min");
    expect(detalheDaMesa(mesa({ status: "ocupada", comandasAbertas: 2 }), agora)).toBe("2 comandas");
  });

  it("calls a bare number Mesa N and keeps a house name as it is", () => {
    expect(nomeDaMesa({ identificacao: "12" })).toBe("Mesa 12");
    expect(nomeDaMesa({ identificacao: " 5 " })).toBe("Mesa 5");
    expect(nomeDaMesa({ identificacao: "Varanda 3" })).toBe("Varanda 3");
  });
});

describe("move answers", () => {
  it.each([
    [200, undefined, true, false],
    [403, undefined, false, false],
    [404, "nao-encontrada", false, true],
    [409, "versao-desatualizada", false, true],
    [409, "comanda-nao-aberta", false, true],
    [409, "mesma-mesa", false, false],
    [409, "mesas-desligadas", false, true],
    [500, undefined, false, false],
  ])("status %s %s: ok=%s, reload=%s", (status, codigo, ok, recarregar) => {
    const r = resultadoDaTroca(status, codigo);

    expect(r.ok).toBe(ok);
    if (!r.ok) {
      expect(r.recarregar).toBe(recarregar);
      expect(r.mensagem.length).toBeGreaterThan(10);
    }
  });

  it("tells a stale version apart and keeps the person's choice by asking to tap Mover again", () => {
    const r = resultadoDaTroca(409, "versao-desatualizada");

    expect(r.ok).toBe(false);
    expect(!r.ok && r.mensagem).toContain("outro aparelho");
    expect(!r.ok && r.mensagem).toContain("Mover");
  });
});
