import { keepPreviousData, useQuery } from "@tanstack/react-query";
import { paraComanda } from "../comandas/tipos";
import { api } from "../sessao/sessao";
import type { ConsultaDaCentral } from "./filtros";

/**
 * The central de comandas: the open comandas of the session's unit that match the filters (the server scopes them to the unit and caps the rows) and the total that matches.
 * The key starts with `["comandas"]`, so the live stream of the list (`useFluxoDeComandas`) refreshes it with no polling of its own; the previous rows stay while a new filter loads.
 */
export function useCentral(consulta: ConsultaDaCentral, enabled: boolean) {
  return useQuery({
    queryKey: ["comandas", "central", consulta],
    enabled,
    placeholderData: keepPreviousData,
    queryFn: async () => {
      const { data } = await api.GET("/comandas/central", { params: { query: consulta } });
      if (!data) {
        throw new Error("central");
      }

      return {
        comandas: data.comandas.map((c) => paraComanda(c as unknown as Record<string, unknown>)),
        total: Number(data.total),
      };
    },
  });
}
