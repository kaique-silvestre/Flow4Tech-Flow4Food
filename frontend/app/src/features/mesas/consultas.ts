import { useQuery } from "@tanstack/react-query";
import { api } from "../sessao/sessao";
import { type MapaDeMesas, paraMapa, type ResultadoDaTroca, resultadoDaTroca } from "./tipos";

/** The table map of the active unit (areas, tables, derived status). Starts with `mesas`, so every place that already refreshes the tables refreshes the map too. */
export function useMapaDeMesas(enabled: boolean) {
  return useQuery<MapaDeMesas>({
    queryKey: ["mesas", "mapa"],
    enabled,
    refetchInterval: 15_000,
    queryFn: async () => {
      const { data } = await api.GET("/mesas/mapa");
      if (!data) {
        throw new Error("mapa");
      }

      return paraMapa(data as unknown as Record<string, unknown>);
    },
  });
}

/** Moves an open comanda to another table of the unit. Needs a connection (it is not queued): who is at which table must be what the server says. */
export async function trocarMesa(comandaId: string, mesaId: string, versao: number): Promise<ResultadoDaTroca> {
  try {
    const { response, error } = await api.POST("/comandas/{comandaId}/trocar-mesa", {
      params: { path: { comandaId } },
      body: { mesaId, versao },
    });
    const codigo = (error as { codigo?: string } | undefined)?.codigo;
    return resultadoDaTroca(response.status, codigo);
  } catch {
    return { ok: false, mensagem: "Sem conexão. Tente de novo quando o sinal voltar.", recarregar: false };
  }
}
