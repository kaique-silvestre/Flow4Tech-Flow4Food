import type { MesaStatus } from "@flow4tech/design-system";
import { tempoDecorrido } from "../comandas/tipos";

/** A table on the map. `status` is derived by the server from the open comandas; `abertaDesde` is the opening of the first one (null when free). */
export interface MesaDoMapa {
  id: string;
  identificacao: string;
  capacidade: number | null;
  areaId: string | null;
  status: MesaStatus;
  comandasAbertas: number;
  abertaDesde: string | null;
}

export interface AreaDoMapa {
  id: string;
  nome: string;
  mesas: MesaDoMapa[];
}

export interface MapaDeMesas {
  areas: AreaDoMapa[];
  semArea: MesaDoMapa[];
}

export function statusDaMesa(valor: unknown): MesaStatus {
  return valor === "ocupada" || valor === "aguardando-conta" ? valor : "livre";
}

function paraMesaDoMapa(m: Record<string, unknown>): MesaDoMapa {
  return {
    id: String(m["id"]),
    identificacao: String(m["identificacao"]),
    capacidade: typeof m["capacidade"] === "number" ? m["capacidade"] : null,
    areaId: typeof m["areaId"] === "string" ? m["areaId"] : null,
    status: statusDaMesa(m["status"]),
    comandasAbertas: Number(m["comandasAbertas"] ?? 0),
    abertaDesde: typeof m["abertaDesde"] === "string" ? m["abertaDesde"] : null,
  };
}

export function paraMapa(bruto: Record<string, unknown>): MapaDeMesas {
  const lista = (valor: unknown) => (Array.isArray(valor) ? (valor as Record<string, unknown>[]) : []);
  return {
    areas: lista(bruto["areas"]).map((a) => ({
      id: String(a["id"]),
      nome: String(a["nome"]),
      mesas: lista(a["mesas"]).map(paraMesaDoMapa),
    })),
    semArea: lista(bruto["semArea"]).map(paraMesaDoMapa),
  };
}

/** Every table of the map, areas first (in the order shown), then the ones without an area. */
export function todasAsMesas(mapa: MapaDeMesas): MesaDoMapa[] {
  return [...mapa.areas.flatMap((a) => a.mesas), ...mapa.semArea];
}

/** The server answers an empty map when the unit does not use tables (or has none registered): there is nothing to show. */
export function semMesas(mapa: MapaDeMesas): boolean {
  return todasAsMesas(mapa).length === 0;
}

/** The second line of a row: how many comandas and since when for a table in use, the seats for a free one (nothing when unknown). */
export function detalheDaMesa(mesa: MesaDoMapa, agora: number): string {
  if (mesa.status === "livre") {
    return mesa.capacidade === null ? "" : `${mesa.capacidade} ${mesa.capacidade === 1 ? "lugar" : "lugares"}`;
  }

  const desde = mesa.abertaDesde === null ? "" : tempoDecorrido(mesa.abertaDesde, agora);
  const comandas = mesa.comandasAbertas > 1 ? `${mesa.comandasAbertas} comandas` : "";
  return [comandas, desde].filter((parte) => parte !== "").join(" · ");
}

/** The name read aloud and written in titles: a bare number is "Mesa 12", a name the house chose stays as it is ("Varanda 3"). */
export function nomeDaMesa(mesa: Pick<MesaDoMapa, "identificacao">): string {
  return /^\d+$/.test(mesa.identificacao.trim()) ? `Mesa ${mesa.identificacao.trim()}` : mesa.identificacao;
}

export type ResultadoDaTroca = { ok: true } | { ok: false; mensagem: string; recarregar: boolean };

/** What to tell the person for each answer of the move. The chosen table stays selected on every failure, so one tap tries again. */
export function resultadoDaTroca(status: number, codigo: string | undefined): ResultadoDaTroca {
  if (status >= 200 && status < 300) {
    return { ok: true };
  }

  if (status === 403) {
    return { ok: false, mensagem: "Você não tem permissão para trocar de mesa.", recarregar: false };
  }

  if (status === 404) {
    return { ok: false, mensagem: "Esta comanda ou a mesa não existe mais. Atualizamos a tela.", recarregar: true };
  }

  if (status === 409) {
    switch (codigo) {
      case "versao-desatualizada":
        return {
          ok: false,
          mensagem:
            "Esta comanda foi alterada em outro aparelho. Atualizamos os dados; toque em Mover para tentar de novo.",
          recarregar: true,
        };
      case "comanda-nao-aberta":
        return { ok: false, mensagem: "Esta comanda já foi fechada.", recarregar: true };
      case "mesma-mesa":
        return { ok: false, mensagem: "A comanda já está nesta mesa. Escolha outra.", recarregar: false };
      case "mesas-desligadas":
        return { ok: false, mensagem: "Esta unidade não usa mesas. Atualizamos a tela.", recarregar: true };
      default:
        break;
    }
  }

  return { ok: false, mensagem: "Não foi possível trocar de mesa. Tente de novo.", recarregar: false };
}
