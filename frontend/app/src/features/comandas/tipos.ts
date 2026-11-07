export interface Comanda {
  id: string;
  numero: number;
  numeroTexto: string;
  identificacao: string;
  mesaId: string | null;
  abertaEm: string;
  versao: number;
  estado: string;
}

export interface Mesa {
  id: string;
  identificacao: string;
  status: "livre" | "ocupada";
}

export function paraComanda(c: Record<string, unknown>): Comanda {
  return {
    id: String(c["id"]),
    numero: Number(c["numero"]),
    numeroTexto: String(c["numeroTexto"]),
    identificacao: String(c["identificacao"]),
    mesaId: (c["mesaId"] as string | null | undefined) ?? null,
    abertaEm: String(c["abertaEm"] ?? ""),
    versao: Number(c["versao"] ?? 1),
    estado: String(c["estado"] ?? "aberta"),
  };
}

export function paraMesa(m: Record<string, unknown>): Mesa {
  return {
    id: String(m["id"]),
    identificacao: String(m["identificacao"]),
    status: m["status"] === "ocupada" ? "ocupada" : "livre",
  };
}

/** "há 12 min", "há 1 h 05 min": elapsed time since the comanda opened, in the words the floor uses. */
export function tempoDecorrido(abertaEm: string, agora: number): string {
  const inicio = new Date(abertaEm).getTime();
  if (Number.isNaN(inicio)) {
    return "";
  }

  const minutos = Math.max(0, Math.floor((agora - inicio) / 60_000));
  if (minutos < 1) {
    return "agora";
  }

  if (minutos < 60) {
    return `há ${minutos} min`;
  }

  const h = Math.floor(minutos / 60);
  const m = minutos % 60;
  return `há ${h} h ${m.toString().padStart(2, "0")} min`;
}

/** The list search: a number ("12", "#12") or part of the name, ignoring case and accents. */
export function combinaBusca(comanda: Comanda, busca: string): boolean {
  const termo = normalizar(busca.trim().replace(/^#/, ""));
  if (termo === "") {
    return true;
  }

  return String(comanda.numero) === termo || normalizar(comanda.identificacao).includes(termo);
}

function normalizar(texto: string): string {
  return texto
    .normalize("NFD")
    .replace(/\p{Diacritic}/gu, "")
    .toLowerCase();
}

/** The mesa an entered number refers to: its identification equals the digits (the seed and the owners name tables by number). */
export function acharMesaPorNumero(mesas: readonly Mesa[], digitos: string): Mesa | undefined {
  return mesas.find((m) => m.identificacao.trim().toLowerCase() === digitos);
}

/** What the floor calls a comanda: the table it holds ("Mesa 6") or the name it was given ("Ana e Beto", "Comanda 7"). */
export function nomeExibido(comanda: Comanda): string {
  return comanda.mesaId === null ? comanda.identificacao : `Mesa ${comanda.identificacao}`;
}
