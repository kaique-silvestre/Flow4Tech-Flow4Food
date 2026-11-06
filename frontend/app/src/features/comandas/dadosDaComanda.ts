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

      return data.itens.map((i) => ({
        id: String(i.id),
        rodadaId: String(i.rodadaId),
        produtoId: String(i.produtoId),
        produtoNome: String(i.produtoNome),
        precoCentavos: Number(i.precoCentavos),
        quantidade: Number(i.quantidade),
        observacao: (i.observacao as string | null) ?? null,
        estado: i.estado === "cancelado" ? "cancelado" : "lancado",
      }));
    },
  });
}
