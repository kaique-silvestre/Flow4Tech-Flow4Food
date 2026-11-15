import type { ComandaStatus } from "@flow4tech/design-system";
import { type Comanda, combinaBusca } from "./tipos";

/**
 * A comanda with no launch for this long shows as "Parada". The 40 minutes are the prototype's value (`STALE_ACT_MIN`); it is a display rule of the list, never a bill rule, and the product may want it
 * as an organization option later (pending owner decision, F4-0132).
 */
export const PARADA_APOS_MINUTOS = 40;

export type Filtro = "minhas" | "todas" | "abertas" | "aguardando";

export interface EstadoDaComanda {
  status: Extract<ComandaStatus, "aberta" | "aguardando-pagamento" | "parada">;
  /** Elapsed time after the word ("há 52 min"), only for Parada. */
  detalhe?: string;
}

/** "agora", "12 min", "2h 31min": the floor's words for a span of minutes. */
export function duracao(minutos: number): string {
  if (minutos < 1) {
    return "agora";
  }

  if (minutos < 60) {
    return `${minutos} min`;
  }

  return `${Math.floor(minutos / 60)}h ${(minutos % 60).toString().padStart(2, "0")}min`;
}

/** The card's time line: "aberta agora" in the first minute, "12 min aberta", "2h 31min aberta" after. */
export function tempoAberta(minutos: number): string {
  return minutos < 1 ? "aberta agora" : `${duracao(minutos)} aberta`;
}

function minutosDesde(quando: string, agora: number): number {
  const inicio = new Date(quando).getTime();
  return Number.isNaN(inicio) ? 0 : Math.max(0, Math.floor((agora - inicio) / 60_000));
}

/** Minutes since the last launch (the opening when the server did not say). */
function minutosParada(comanda: Comanda, agora: number): number {
  return minutosDesde(comanda.ultimaAtividadeEm ?? comanda.abertaEm, agora);
}

/** Minutes the comanda has been open, for the card. */
export function minutosAberta(comanda: Comanda, agora: number): number {
  return minutosDesde(comanda.abertaEm, agora);
}

/** The word of the card: waiting for payment once the bill was requested, Parada when idle too long, Aberta otherwise. Never a color alone: the badge always carries the word. */
export function estadoDaComanda(comanda: Comanda, agora: number): EstadoDaComanda {
  if (comanda.contaPedidaEm !== null) {
    return { status: "aguardando-pagamento" };
  }

  const parada = minutosParada(comanda, agora);
  return parada >= PARADA_APOS_MINUTOS ? { status: "parada", detalhe: `há ${duracao(parada)}` } : { status: "aberta" };
}

/** Who holds the comanda for the card: "Você" for the caller's own, the waiter's name for the others'. */
export function quemAtende(comanda: Comanda, contaDaSessao: string | null): string {
  return contaDaSessao !== null && comanda.garcomId === contaDaSessao ? "Você" : (comanda.garcomNome ?? "");
}

const ordemDoEstado = { aberta: 0, parada: 0, "aguardando-pagamento": 1 } as const;

/** Who is looking: the account of the session and whether the server shows them the others' comandas. Without that permission the server already sends only their own, so "Minhas" is the whole list. */
export interface Quem {
  conta: string | null;
  verTodas: boolean;
}

function bate(comanda: Comanda, filtro: Filtro, quem: Quem): boolean {
  switch (filtro) {
    case "minhas":
      return !quem.verTodas || (quem.conta !== null && comanda.garcomId === quem.conta);
    case "abertas":
      return comanda.contaPedidaEm === null;
    case "aguardando":
      return comanda.contaPedidaEm !== null;
    case "todas":
      return true;
  }
}

/** The tally on each chip: over the whole list the server sent (the caller's visibility is already applied there), never over the search text. */
export function contar(comandas: readonly Comanda[], quem: Quem): Record<Filtro, number> {
  const por = (filtro: Filtro) => comandas.filter((c) => bate(c, filtro, quem)).length;
  return { minhas: por("minhas"), todas: comandas.length, abertas: por("abertas"), aguardando: por("aguardando") };
}

/** The list the person sees: the chosen filter, then the search, stalled and open first (the longest idle on top), then the ones waiting for payment. */
export function filtrar(
  comandas: readonly Comanda[],
  filtro: Filtro,
  busca: string,
  quem: Quem,
  agora: number,
): Comanda[] {
  return comandas
    .filter((c) => bate(c, filtro, quem) && combinaBusca(c, busca))
    .map((c) => ({ c, grupo: ordemDoEstado[estadoDaComanda(c, agora).status], parada: minutosParada(c, agora) }))
    .sort((a, b) => a.grupo - b.grupo || b.parada - a.parada)
    .map(({ c }) => c);
}

/** The chips, in order. "Todas" is only for who may see the others' comandas: for the rest the server already sends only their own. */
export function filtrosOferecidos(verTodas: boolean): Filtro[] {
  return verTodas ? ["minhas", "todas", "abertas", "aguardando"] : ["minhas", "abertas", "aguardando"];
}

export function filtroInicial(verTodas: boolean): Filtro {
  return verTodas ? "todas" : "minhas";
}
