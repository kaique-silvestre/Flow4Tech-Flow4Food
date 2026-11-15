import { useQuery } from "@tanstack/react-query";
import { api, useSessao } from "./sessao";

export interface ContextoDaSessao {
  organizacao: string;
  unidade: string;
  usuario: string;
}

/**
 * The names the header shows (company, unit, person), read from the server for the session: organization and unit never come from the client. While it is unknown (loading, offline, refused) it is `undefined`
 * and the header simply does not draw the line: a guess would be worse than nothing.
 */
export function useContextoDaSessao(): ContextoDaSessao | undefined {
  const sessao = useSessao();
  const unidade = sessao.status === "ativa" ? sessao.unidadeAtivaId : null;
  const consulta = useQuery<ContextoDaSessao>({
    queryKey: ["sessao", "contexto", unidade],
    enabled: unidade !== null,
    staleTime: 5 * 60_000,
    queryFn: async () => {
      const { data } = await api.GET("/sessao/contexto");
      if (!data) {
        throw new Error("contexto");
      }

      return { organizacao: data.organizacao, unidade: data.unidade, usuario: data.usuario };
    },
  });
  return consulta.data;
}
