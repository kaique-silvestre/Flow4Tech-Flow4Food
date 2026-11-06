import { useQuery } from "@tanstack/react-query";
import { api } from "../sessao/sessao";
import { paraComanda, paraMesa, type Comanda, type Mesa } from "./tipos";

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
