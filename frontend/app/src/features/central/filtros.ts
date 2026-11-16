import type { Comanda } from "../comandas/tipos";

type Vista = "cartoes" | "tabela";

/** What the address of the central carries (`?garcom=…&mesa=…&tempo=45&numero=12&vista=tabela`): only what is filled, so a link reopens the same view. */
export interface BuscaDaCentral {
  garcom?: string;
  mesa?: string;
  tempo?: string;
  numero?: string;
  vista?: "tabela";
}

/** The filters as the screen holds them: every field a string, empty meaning "not filtered". */
export interface FiltrosDaCentral {
  garcom: string;
  mesa: string;
  tempo: string;
  numero: string;
  vista: Vista;
}

/** What `GET /comandas/central` takes (query strings, the server parses them). */
export interface ConsultaDaCentral {
  garcomId?: string;
  mesaId?: string;
  tempoMinimoMinutos?: string;
  numero?: string;
}

const texto = (valor: unknown) => (typeof valor === "string" || typeof valor === "number" ? String(valor).trim() : "");

/** Minutes and number are whole numbers: anything else typed or pasted is dropped (the server would answer 400 to it). Leading zeros go too. */
export function soDigitos(valor: string): string {
  const digitos = valor.replace(/\D/g, "").replace(/^0+(?=\d)/, "");
  return digitos.slice(0, 9);
}

/** The address's search as filters; anything unknown or malformed is ignored, never an error screen. */
export function lerFiltros(bruto: Record<string, unknown>): FiltrosDaCentral {
  return {
    garcom: texto(bruto["garcom"]),
    mesa: texto(bruto["mesa"]),
    tempo: soDigitos(texto(bruto["tempo"])),
    numero: soDigitos(texto(bruto["numero"])),
    vista: bruto["vista"] === "tabela" ? "tabela" : "cartoes",
  };
}

/** The filters as the address keeps them: no empty key, and the default view (cards) left out. */
export function paraBusca(filtros: FiltrosDaCentral): BuscaDaCentral {
  return {
    ...(filtros.garcom ? { garcom: filtros.garcom } : {}),
    ...(filtros.mesa ? { mesa: filtros.mesa } : {}),
    ...(filtros.tempo ? { tempo: filtros.tempo } : {}),
    ...(filtros.numero ? { numero: filtros.numero } : {}),
    ...(filtros.vista === "tabela" ? { vista: "tabela" as const } : {}),
  };
}

/** The query the server gets: only the filled filters. The view is the screen's, never the server's. */
export function paraConsulta(filtros: FiltrosDaCentral): ConsultaDaCentral {
  return {
    ...(filtros.garcom ? { garcomId: filtros.garcom } : {}),
    ...(filtros.mesa ? { mesaId: filtros.mesa } : {}),
    ...(filtros.tempo ? { tempoMinimoMinutos: filtros.tempo } : {}),
    ...(filtros.numero ? { numero: filtros.numero } : {}),
  };
}

export function haFiltro(filtros: FiltrosDaCentral): boolean {
  return Object.keys(paraConsulta(filtros)).length > 0;
}

/** The count line: the server's total of open comandas matching the filters; says so when the rows are fewer than that (the server caps them). */
export function textoDoTotal(total: number, mostradas: number): string {
  const comandas = `${total} ${total === 1 ? "comanda aberta" : "comandas abertas"}`;
  return mostradas < total ? `${comandas}, mostrando as ${mostradas} mais recentes` : comandas;
}

export interface OpcaoDeGarcom {
  id: string;
  nome: string;
}

/** The waiters the select offers: everyone seen on the central so far (filtering by one makes the rows only his, the others must stay choosable) plus the one chosen, by name. */
export function juntarGarcons(
  conhecidos: readonly OpcaoDeGarcom[],
  comandas: readonly Comanda[],
  escolhido: string,
): OpcaoDeGarcom[] {
  const porId = new Map(conhecidos.map((g) => [g.id, g] as const));
  for (const c of comandas) {
    if (c.garcomId !== "" && (c.garcomNome ?? "") !== "") {
      porId.set(c.garcomId, { id: c.garcomId, nome: c.garcomNome ?? "" });
    }
  }

  if (escolhido !== "" && !porId.has(escolhido)) {
    porId.set(escolhido, { id: escolhido, nome: "Garçom escolhido" });
  }

  return [...porId.values()].sort((a, b) => a.nome.localeCompare(b.nome, "pt-BR"));
}
