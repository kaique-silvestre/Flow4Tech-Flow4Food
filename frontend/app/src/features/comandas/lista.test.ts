import { describe, expect, it } from "vitest";
import {
  contar,
  duracao,
  estadoDaComanda,
  filtrar,
  filtroInicial,
  filtrosOferecidos,
  PARADA_APOS_MINUTOS,
  quemAtende,
  tempoAberta,
} from "./lista";
import { type Comanda, paraComanda } from "./tipos";

const AGORA = Date.parse("2026-11-15T20:00:00Z");
const minutosAtras = (m: number) => new Date(AGORA - m * 60_000).toISOString();

function comanda(extra: Partial<Record<keyof Comanda, unknown>> & { id: string }): Comanda {
  return paraComanda({
    numero: 1,
    numeroTexto: "#1",
    identificacao: "Mesa 1",
    garcomId: "eu",
    garcomNome: "Marina",
    abertaEm: minutosAtras(120),
    ultimaAtividadeEm: minutosAtras(5),
    consumoCentavos: 0,
    ...extra,
  });
}

describe("duracao", () => {
  it.each([
    [0, "agora"],
    [12, "12 min"],
    [59, "59 min"],
    [60, "1h 00min"],
    [151, "2h 31min"],
  ])("says %i minutes as %s", (minutos, texto) => {
    expect(duracao(minutos)).toBe(texto);
  });
});

describe("tempoAberta", () => {
  it("says aberta agora in the first minute and the time open after", () => {
    expect(tempoAberta(0)).toBe("aberta agora");
    expect(tempoAberta(12)).toBe("12 min aberta");
    expect(tempoAberta(135)).toBe("2h 15min aberta");
  });
});

describe("estadoDaComanda", () => {
  it("is Aberta while something was launched recently", () => {
    expect(estadoDaComanda(comanda({ id: "a" }), AGORA)).toEqual({ status: "aberta" });
  });

  it("turns Parada, with the minutes in words, when nothing was launched for the threshold", () => {
    const parada = comanda({ id: "p", ultimaAtividadeEm: minutosAtras(PARADA_APOS_MINUTOS) });

    expect(estadoDaComanda(parada, AGORA)).toEqual({ status: "parada", detalhe: `há ${PARADA_APOS_MINUTOS} min` });
    expect(
      estadoDaComanda(comanda({ id: "q", ultimaAtividadeEm: minutosAtras(PARADA_APOS_MINUTOS - 1) }), AGORA),
    ).toEqual({ status: "aberta" });
    expect(estadoDaComanda(comanda({ id: "r", ultimaAtividadeEm: minutosAtras(72) }), AGORA)).toEqual({
      status: "parada",
      detalhe: "há 1h 12min",
    });
  });

  it("is Aguardando pagamento once the bill was requested, even if it is also idle", () => {
    const aguardando = comanda({ id: "g", contaPedidaEm: minutosAtras(3), ultimaAtividadeEm: minutosAtras(90) });

    expect(estadoDaComanda(aguardando, AGORA)).toEqual({ status: "aguardando-pagamento" });
  });

  it("falls back to the opening when the server sent no last launch", () => {
    const semAtividade = comanda({ id: "s", ultimaAtividadeEm: null, abertaEm: minutosAtras(100) });

    expect(estadoDaComanda(semAtividade, AGORA).status).toBe("parada");
  });
});

describe("quemAtende", () => {
  it("says Você for the caller's own comanda and the waiter's name for the others", () => {
    expect(quemAtende(comanda({ id: "a" }), "eu")).toBe("Você");
    expect(quemAtende(comanda({ id: "b", garcomId: "outra", garcomNome: "Bianca" }), "eu")).toBe("Bianca");
    expect(quemAtende(comanda({ id: "c", garcomId: "outra", garcomNome: null }), "eu")).toBe("");
  });
});

describe("filtros", () => {
  const minha = comanda({ id: "1", garcomId: "eu", identificacao: "Mesa 3" });
  const outraAberta = comanda({ id: "2", garcomId: "bia", garcomNome: "Bianca", identificacao: "Mesa 5", numero: 2 });
  const outraParada = comanda({
    id: "3",
    garcomId: "bia",
    garcomNome: "Bianca",
    identificacao: "Dona Lúcia",
    numero: 3,
    ultimaAtividadeEm: minutosAtras(60),
  });
  const aguardando = comanda({
    id: "4",
    garcomId: "eu",
    identificacao: "Balcão 1",
    numero: 4,
    contaPedidaEm: minutosAtras(2),
  });
  const todas = [minha, outraAberta, outraParada, aguardando];
  const ve = { conta: "eu", verTodas: true };

  it("counts each chip over the whole list, not over the search", () => {
    expect(contar(todas, ve)).toEqual({ minhas: 2, todas: 4, abertas: 3, aguardando: 1 });
  });

  it("Minhas keeps the caller's comandas, Abertas the ones not waiting for payment (stalled ones included), Aguardando the ones with a requested bill", () => {
    expect(filtrar(todas, "minhas", "", ve, AGORA).map((c) => c.id)).toEqual(["1", "4"]);
    expect(filtrar(todas, "abertas", "", ve, AGORA).map((c) => c.id)).toEqual(["3", "1", "2"]);
    expect(filtrar(todas, "aguardando", "", ve, AGORA).map((c) => c.id)).toEqual(["4"]);
    expect(filtrar(todas, "todas", "", ve, AGORA)).toHaveLength(4);
  });

  it("keeps the whole list under Minhas for who only sees their own (the server already narrowed it)", () => {
    const soMinhas = { conta: "eu", verTodas: false };

    expect(filtrar(todas, "minhas", "", soMinhas, AGORA)).toHaveLength(4);
    expect(contar(todas, soMinhas).minhas).toBe(4);
  });

  it("lists stalled first (the longest idle on top), then the open ones, then the ones waiting", () => {
    expect(filtrar(todas, "todas", "", ve, AGORA).map((c) => c.id)).toEqual(["3", "1", "2", "4"]);
  });

  it("searches by name or number inside the chosen filter, ignoring case and accents", () => {
    expect(filtrar(todas, "todas", "lucia", ve, AGORA).map((c) => c.id)).toEqual(["3"]);
    expect(filtrar(todas, "minhas", "lucia", ve, AGORA)).toEqual([]);
    expect(filtrar(todas, "todas", "#4", ve, AGORA).map((c) => c.id)).toEqual(["4"]);
  });

  it("offers Todas only to who may see the others' comandas, and starts on it", () => {
    expect(filtrosOferecidos(true)).toEqual(["minhas", "todas", "abertas", "aguardando"]);
    expect(filtrosOferecidos(false)).toEqual(["minhas", "abertas", "aguardando"]);
    expect(filtroInicial(true)).toBe("todas");
    expect(filtroInicial(false)).toBe("minhas");
  });
});
