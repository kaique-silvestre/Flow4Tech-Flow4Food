import { formatBRL } from "@flow4tech/design-system";
import { useQuery } from "@tanstack/react-query";
import { api } from "../sessao/sessao";

export interface EventoDoHistorico {
  id: string;
  acao: string;
  quando: string;
  usuarioNome: string | null;
  antes: Record<string, string>;
  depois: Record<string, string>;
}

export interface Garcom {
  id: string;
  nome: string;
}

/** The timeline of a comanda (GET /comandas/{id}/historico), oldest first as the server sends it. Refreshed with the rest of the comanda when a queued action finishes. */
export function useHistorico(comandaId: string, enabled: boolean) {
  return useQuery<EventoDoHistorico[]>({
    queryKey: ["comanda", comandaId, "historico"],
    enabled,
    queryFn: async () => {
      const { data } = await api.GET("/comandas/{comandaId}/historico", { params: { path: { comandaId } } });
      if (!data) {
        throw new Error("historico");
      }

      return data.eventos.map((e) => ({
        id: String(e.id),
        acao: String(e.acao),
        quando: String(e.quando),
        usuarioNome: (e.usuarioNome as string | null) ?? null,
        antes: { ...(e.antes as Record<string, string>) },
        depois: { ...(e.depois as Record<string, string>) },
      }));
    },
  });
}

/** The active accounts of the unit a comanda can be handed to. */
export function useGarcons(enabled: boolean) {
  return useQuery<Garcom[]>({
    queryKey: ["comandas", "garcons"],
    enabled,
    staleTime: 60_000,
    queryFn: async () => {
      const { data } = await api.GET("/comandas/garcons");
      if (!data) {
        throw new Error("garcons");
      }

      return data.map((g) => ({ id: String(g.id), nome: String(g.nome) }));
    },
  });
}

export interface LinhaDoHistorico {
  titulo: string;
  detalhe: string | null;
}

const dinheiro = (valor: string | undefined) => (valor === undefined ? null : formatBRL(Number(valor)));

const juntar = (...partes: (string | null | undefined)[]) => {
  const feitas = partes.filter((p): p is string => typeof p === "string" && p !== "");
  return feitas.length > 0 ? feitas.join(" · ") : null;
};

const deAte = (antes: string | undefined, depois: string | undefined) =>
  antes !== undefined && depois !== undefined ? `${antes} → ${depois}` : (depois ?? null);

/** One event in the words of the floor: what happened and the detail worth reading (never a raw code, never the whole record). */
export function linhaDoHistorico(e: Pick<EventoDoHistorico, "acao" | "antes" | "depois">): LinhaDoHistorico {
  const { antes, depois } = e;
  switch (e.acao) {
    case "comanda:abrir":
      return { titulo: "Comanda aberta", detalhe: juntar(depois["identificacao"]) };
    case "comanda:lancar-rodada":
      return { titulo: "Rodada enviada", detalhe: dinheiro(depois["valorCentavos"]) };
    case "comanda:desfazer-rodada":
      return { titulo: "Rodada desfeita", detalhe: null };
    case "comanda:cancelar-item":
      return { titulo: "Item cancelado", detalhe: juntar(depois["produto"], depois["motivo"]) };
    case "comanda:alterar-item": {
      const quantidade = deAte(antes["quantidade"], depois["quantidade"]);
      return {
        titulo: "Item alterado",
        detalhe: juntar(depois["produto"], quantidade === null ? null : `${quantidade} un.`, depois["observacao"]),
      };
    }
    case "comanda:renomear":
      return { titulo: "Comanda renomeada", detalhe: deAte(antes["identificacao"], depois["identificacao"]) };
    case "comanda:transferir-mesa":
      return { titulo: "Mudou de mesa", detalhe: juntar(depois["identificacao"]) };
    case "comanda:trocar-garcom":
      return { titulo: "Garçom trocado", detalhe: null };
    case "comanda:alterar-dados":
      return {
        titulo: "Dados alterados",
        detalhe: juntar(
          depois["pessoas"] ? `${depois["pessoas"]} pessoas` : "sem número de pessoas",
          depois["observacao"],
        ),
      };
    case "comanda:remover-taxa-servico":
      return { titulo: "Taxa de serviço removida", detalhe: null };
    case "pagamento:registrar":
      return { titulo: "Pagamento recebido", detalhe: dinheiro(depois["aplicadoCentavos"]) };
    case "comanda:fechar":
      return { titulo: "Conta fechada", detalhe: dinheiro(depois["totalCentavos"]) };
    case "comanda:cancelar":
      return { titulo: "Comanda cancelada", detalhe: juntar(depois["motivo"]) };
    default:
      return { titulo: e.acao, detalhe: null };
  }
}

/** "21:34" for today, "11/11 21:34" for another day (the floor reads the time first). */
export function horaDoEvento(iso: string, agora: Date): string {
  const quando = new Date(iso);
  if (Number.isNaN(quando.getTime())) {
    return "";
  }

  const hora = quando.toLocaleTimeString("pt-BR", { hour: "2-digit", minute: "2-digit" });
  if (quando.toDateString() === agora.toDateString()) {
    return hora;
  }

  return `${quando.toLocaleDateString("pt-BR", { day: "2-digit", month: "2-digit" })} ${hora}`;
}
