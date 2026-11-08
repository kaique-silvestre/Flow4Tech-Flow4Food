import { useQuery } from "@tanstack/react-query";
import { api } from "../sessao/sessao";
import { paraItemDaConta } from "./dadosDaComanda";
import type { ItemDaConta } from "./rodada";
import { type Comanda, type Mesa, paraComanda, paraMesa } from "./tipos";

export function useComandas(enabled: boolean) {
  return useQuery<Comanda[]>({
    queryKey: ["comandas"],
    enabled,
    refetchInterval: 15_000,
    queryFn: async () => {
      const { data } = await api.GET("/comandas");
      if (!data) {
        throw new Error("comandas");
      }

      return data.map((c) => paraComanda(c as unknown as Record<string, unknown>));
    },
  });
}

/** The unit's tables with their derived status. An empty list means the unit works without tables (or the caller may not see them): the keypad then names the comanda by number. */
export function useMesas(enabled: boolean) {
  return useQuery<Mesa[]>({
    queryKey: ["mesas"],
    enabled,
    queryFn: async () => {
      const { data, response } = await api.GET("/mesas");
      if (data) {
        return data.map((m) => paraMesa(m as unknown as Record<string, unknown>));
      }

      if (response.status === 403) {
        return [];
      }

      throw new Error("mesas");
    },
  });
}

/** What was consumed the last time this table closed. `itens` empty / `comandaId` null means no history: the card stays hidden. */
export interface UltimaVez {
  comandaId: string;
  numeroTexto: string;
  fechadaEm: string | null;
  itens: ItemDaConta[];
}

/** "Última vez nesta mesa": the last closed bill of the table (only the items that stayed). Resolves to null when the table has no history. */
export function useUltimaVez(mesaId: string | null, enabled: boolean) {
  return useQuery<UltimaVez | null>({
    queryKey: ["mesa", mesaId, "ultima-vez"],
    enabled: enabled && mesaId !== null,
    staleTime: 60_000,
    queryFn: async () => {
      const { data } = await api.GET("/mesas/{mesaId}/ultima-vez", { params: { path: { mesaId: mesaId ?? "" } } });
      if (!data) {
        throw new Error("ultima-vez");
      }

      if (data.comandaId === null || data.itens.length === 0) {
        return null;
      }

      return {
        comandaId: String(data.comandaId),
        numeroTexto: String(data.numeroTexto ?? ""),
        fechadaEm: data.fechadaEm ?? null,
        itens: data.itens.map(paraItemDaConta),
      };
    },
  });
}
