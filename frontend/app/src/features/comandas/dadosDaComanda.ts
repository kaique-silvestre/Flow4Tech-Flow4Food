import type { components } from "@flow4tech/api-client";
import { useQuery } from "@tanstack/react-query";
import { api } from "../sessao/sessao";
import type { ContaDaComanda, ItemDaConta } from "./rodada";

export function useConta(comandaId: string, enabled: boolean) {
  return useQuery<ContaDaComanda>({
    queryKey: ["comanda", comandaId, "conta"],
    enabled,
    queryFn: async () => {
      const { data } = await api.GET("/comandas/{comandaId}/conta", { params: { path: { comandaId } } });
      if (!data) {
        throw new Error("conta");
      }

      return {
        consumoCentavos: Number(data.consumoCentavos),
        taxaServicoCentavos: Number(data.taxaServicoCentavos),
        totalCentavos: Number(data.totalCentavos),
        pagoCentavos: Number(data.pagoCentavos),
        saldoCentavos: Number(data.saldoCentavos),
        taxaServicoRemovida: Boolean(data.taxaServicoRemovida),
        taxaServicoPercentual: Number(data.taxaServicoPercentual),
        versao: Number(data.versao),
        estado: String(data.estado),
        descontoCentavos: Number(data.descontoCentavos ?? 0),
      };
    },
  });
}

export function useItens(comandaId: string, enabled: boolean) {
  return useQuery<ItemDaConta[]>({
    queryKey: ["comanda", comandaId, "itens"],
    enabled,
    queryFn: async () => {
      const { data } = await api.GET("/comandas/{comandaId}/itens", { params: { path: { comandaId } } });
      if (!data) {
        throw new Error("itens");
      }

      return data.itens.map(paraItemDaConta);
    },
  });
}

type ItemDoServidor = components["schemas"]["ItemDto"];

/** One item as the bill shows it: the server total is (price + add-ons) x quantity, so the unit the row multiplies is the same sum. */
export function paraItemDaConta(i: ItemDoServidor): ItemDaConta {
  const opcoes = (i.opcoes ?? []).map((o) => ({
    id: String(o.opcaoId),
    grupoId: String(o.grupoId),
    nome: String(o.nome),
    precoCentavos: Number(o.precoCentavos),
  }));
  const tipo = i.tipo === "cortesia" || i.tipo === "brinde" ? i.tipo : "normal";
  return {
    id: String(i.id),
    rodadaId: String(i.rodadaId),
    produtoId: String(i.produtoId),
    produtoNome: String(i.produtoNome),
    precoCentavos: Number(i.precoCentavos),
    quantidade: Number(i.quantidade),
    observacao: (i.observacao as string | null) ?? null,
    estado: i.estado === "cancelado" ? "cancelado" : "lancado",
    opcoes,
    unitarioCentavos: Number(i.precoCentavos) + opcoes.reduce((soma, o) => soma + o.precoCentavos, 0),
    tipo,
    motivoDoTipo: (i.motivoDoTipo as string | null | undefined) ?? null,
    valorDeTabelaCentavos: Number(i.valorDeTabelaCentavos ?? 0),
  };
}
