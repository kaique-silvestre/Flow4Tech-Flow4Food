import { useQuery } from "@tanstack/react-query";
import { api } from "../sessao/sessao";
import {
  type DetalheDoTurno,
  type Movimento,
  paraDetalhe,
  paraMovimento,
  paraResumo,
  paraTurno,
  type Resumo,
  type Turno,
} from "./logica";

export type Leitura = { tipo: "turno"; resumo: Resumo } | { tipo: "fechado" } | { tipo: "semPermissao" };

/** The open shift of the caller with what the screen needs: the summary (blind-aware), 404 = closed, 403 = no permission. */
export function useLeituraDoCaixa() {
  return useQuery<Leitura>({
    queryKey: ["caixa", "atual"],
    queryFn: async () => {
      const { data, response } = await api.GET("/caixa/turnos/atual/resumo");
      if (data) {
        return { tipo: "turno", resumo: paraResumo(data) };
      }

      if (response.status === 404) {
        return { tipo: "fechado" };
      }

      if (response.status === 403) {
        return { tipo: "semPermissao" };
      }

      throw new Error("caixa");
    },
  });
}

/** The supplies and withdrawals of the open shift, oldest first as the server sends them. */
export function useMovimentos(enabled: boolean) {
  return useQuery<Movimento[]>({
    queryKey: ["caixa", "movimentos"],
    enabled,
    queryFn: async () => {
      const { data } = await api.GET("/caixa/turnos/atual/movimentos");
      if (!data) {
        throw new Error("movimentos");
      }

      return data.map(paraMovimento);
    },
  });
}

/** The closed shifts waiting for a decision (caixa:ver-todos). */
export function usePendentes(enabled: boolean) {
  return useQuery<Turno[]>({
    queryKey: ["caixa", "pendentes"],
    enabled,
    queryFn: async () => {
      const { data } = await api.GET("/caixa/turnos/pendentes");
      if (!data) {
        throw new Error("pendentes");
      }

      return data.map(paraTurno);
    },
  });
}

/** One shift with its counts, movements and decisions. */
export function useDetalheDoTurno(id: string | null) {
  return useQuery<DetalheDoTurno>({
    queryKey: ["caixa", "turno", id],
    enabled: id !== null,
    queryFn: async () => {
      const { data } = await api.GET("/caixa/turnos/{id}", { params: { path: { id: id ?? "" } } });
      if (!data) {
        throw new Error("turno");
      }

      return paraDetalhe(data);
    },
  });
}
