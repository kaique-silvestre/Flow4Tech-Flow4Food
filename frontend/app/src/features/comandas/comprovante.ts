import type { components } from "@flow4tech/api-client";
import { useQuery } from "@tanstack/react-query";
import { api } from "../sessao/sessao";

type ComprovanteDoServidor = components["schemas"]["ComprovanteDto"];

export type TipoDeComprovante = "pre-conta" | "recibo";

interface ItemDoComprovante {
  descricao: string;
  quantidade: number;
  unitarioCentavos: number;
  totalCentavos: number;
  tipo: string;
  observacao: string | null;
  opcoes: string[];
}

export interface Comprovante {
  tipo: string;
  segundaVia: boolean;
  aviso: string;
  numeroTexto: string;
  identificacao: string;
  abertaEm: string;
  fechadaEm: string | null;
  contaPedidaEm: string | null;
  itens: ItemDoComprovante[];
  descontos: { alvo: string; aplicadoCentavos: number; motivo: string }[];
  conta: {
    consumoCentavos: number;
    descontoCentavos: number;
    taxaServicoCentavos: number;
    taxaServicoRemovida: boolean;
    taxaServicoPercentual: number;
    totalCentavos: number;
    pagoCentavos: number;
    saldoCentavos: number;
  };
  pagamentos: { metodoNome: string; aplicadoCentavos: number; trocoCentavos: number; gorjetaCentavos: number }[];
  partes: { numero: number; totalCentavos: number; pagoCentavos: number }[] | null;
}

/** The document exactly as the server wrote it (the screen never recomputes a number). */
export function paraComprovante(d: ComprovanteDoServidor): Comprovante {
  return {
    tipo: d.tipo,
    segundaVia: Boolean(d.segundaVia),
    aviso: d.aviso,
    numeroTexto: d.numeroTexto,
    identificacao: d.identificacao,
    abertaEm: d.abertaEm,
    fechadaEm: d.fechadaEm ?? null,
    contaPedidaEm: d.contaPedidaEm ?? null,
    itens: d.itens.map((i) => ({
      descricao: i.descricao,
      quantidade: Number(i.quantidade),
      unitarioCentavos: Number(i.unitarioCentavos),
      totalCentavos: Number(i.totalCentavos),
      tipo: i.tipo,
      observacao: i.observacao ?? null,
      opcoes: i.opcoes ?? [],
    })),
    descontos: (d.descontos ?? []).map((x) => ({
      alvo: x.alvo,
      aplicadoCentavos: Number(x.aplicadoCentavos),
      motivo: x.motivo,
    })),
    conta: {
      consumoCentavos: Number(d.conta.consumoCentavos),
      descontoCentavos: Number(d.conta.descontoCentavos),
      taxaServicoCentavos: Number(d.conta.taxaServicoCentavos),
      taxaServicoRemovida: Boolean(d.conta.taxaServicoRemovida),
      taxaServicoPercentual: Number(d.conta.taxaServicoPercentual),
      totalCentavos: Number(d.conta.totalCentavos),
      pagoCentavos: Number(d.conta.pagoCentavos),
      saldoCentavos: Number(d.conta.saldoCentavos),
    },
    pagamentos: (d.pagamentos ?? []).map((p) => ({
      metodoNome: p.metodoNome,
      aplicadoCentavos: Number(p.aplicadoCentavos),
      trocoCentavos: Number(p.trocoCentavos),
      gorjetaCentavos: Number(p.gorjetaCentavos),
    })),
    partes: d.partes
      ? d.partes.map((p) => ({
          numero: Number(p.numero),
          totalCentavos: Number(p.totalCentavos),
          pagoCentavos: Number(p.pagoCentavos),
        }))
      : null,
  };
}

export function rotuloDoItem(tipo: string): string | null {
  return tipo === "cortesia" ? "Cortesia" : tipo === "brinde" ? "Brinde" : null;
}

/** "21:05" in the device's zone; empty when the date is not valid. */
export function horaCurta(iso: string): string {
  const d = new Date(iso);
  return Number.isNaN(d.getTime()) ? "" : d.toLocaleTimeString("pt-BR", { hour: "2-digit", minute: "2-digit" });
}

export type FalhaDoComprovante = "proibido" | "nao-encontrada" | "estado" | "rede" | "outro";

/** What went wrong, by the HTTP status (null = the request never got an answer: no signal). */
export function classificarFalha(status: number | null): FalhaDoComprovante {
  if (status === null) {
    return "rede";
  }

  return status === 403 ? "proibido" : status === 404 ? "nao-encontrada" : status === 409 ? "estado" : "outro";
}

export class ErroDoComprovante extends Error {
  readonly falha: FalhaDoComprovante;
  constructor(falha: FalhaDoComprovante) {
    super(falha);
    this.falha = falha;
  }
}

export function falhaDe(erro: unknown): FalhaDoComprovante {
  return erro instanceof ErroDoComprovante ? erro.falha : "rede";
}

/** The words the screen uses for each failure (403 never says why; 409 depends on which document was asked for). */
export function textoDaFalha(falha: FalhaDoComprovante, tipo: TipoDeComprovante): string {
  switch (falha) {
    case "proibido":
      return tipo === "recibo"
        ? "Você não tem permissão para reabrir o recibo."
        : "Você não tem permissão para ver a pré-conta.";
    case "nao-encontrada":
      return "Comanda não encontrada.";
    case "estado":
      return tipo === "recibo" ? "Esta comanda ainda não foi fechada." : "Esta comanda já não está aberta.";
    case "rede":
      return "Sem conexão com o servidor. Tente de novo quando o sinal voltar.";
    default:
      return tipo === "recibo" ? "Não foi possível carregar o recibo." : "Não foi possível carregar a pré-conta.";
  }
}

const CAMINHO = {
  "pre-conta": "/comandas/{comandaId}/pre-conta",
  recibo: "/comandas/{comandaId}/recibo",
} as const;
const CAMINHO_TEXTO = {
  "pre-conta": "/comandas/{comandaId}/pre-conta/texto",
  recibo: "/comandas/{comandaId}/recibo/texto",
} as const;

async function chamar<T>(rodar: () => Promise<{ data?: T | undefined; response: Response }>): Promise<T> {
  let r: { data?: T | undefined; response: Response };
  try {
    r = await rodar();
  } catch {
    throw new ErroDoComprovante("rede");
  }

  if (r.data === undefined) {
    throw new ErroDoComprovante(classificarFalha(r.response.status));
  }

  return r.data;
}

/** The pre-bill (open comanda) or the receipt (closed one) as data. It is read on demand and never cached for long: a pre-bill is a point-in-time reading. */
export function useComprovante(tipo: TipoDeComprovante, comandaId: string, enabled: boolean) {
  return useQuery<Comprovante, ErroDoComprovante>({
    queryKey: ["comanda", comandaId, tipo],
    enabled,
    retry: false,
    gcTime: 0,
    queryFn: async () => {
      const dado = await chamar(() => {
        const params = { params: { path: { comandaId } } };
        return tipo === "recibo" ? api.GET(CAMINHO.recibo, params) : api.GET(CAMINHO["pre-conta"], params);
      });
      return paraComprovante(dado);
    },
  });
}

/** The 40-column plain text the server wrote, for the share sheet or the clipboard. */
export async function buscarTexto(tipo: TipoDeComprovante, comandaId: string): Promise<string> {
  const params = { params: { path: { comandaId } }, parseAs: "text" as const };
  return chamar<string>(async () => {
    const r =
      tipo === "recibo"
        ? await api.GET(CAMINHO_TEXTO.recibo, params)
        : await api.GET(CAMINHO_TEXTO["pre-conta"], params);
    return { data: r.data as string | undefined, response: r.response };
  });
}

/** Marks the bill as requested. 201 the first time, 200 after: both are "requested". */
export async function pedirConta(comandaId: string): Promise<{ contaPedidaEm: string | null }> {
  const dado = await chamar(() => api.POST("/comandas/{comandaId}/pedir-conta", { params: { path: { comandaId } } }));
  return { contaPedidaEm: dado.contaPedidaEm ?? null };
}

export type ResultadoDoCompartilhar = "compartilhado" | "copiado" | "cancelado" | "indisponivel";

/** The phone's share sheet when there is one; otherwise the clipboard. Dismissing the sheet is not an error. */
export async function compartilharTexto(titulo: string, texto: string): Promise<ResultadoDoCompartilhar> {
  if (typeof navigator.share === "function") {
    try {
      await navigator.share({ title: titulo, text: texto });
      return "compartilhado";
    } catch (e) {
      if (e instanceof DOMException && e.name === "AbortError") {
        return "cancelado";
      }
    }
  }

  try {
    await navigator.clipboard.writeText(texto);
    return "copiado";
  } catch {
    return "indisponivel";
  }
}
