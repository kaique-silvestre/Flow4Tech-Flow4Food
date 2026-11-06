import { useQuery } from "@tanstack/react-query";
import { api, useSessao } from "./sessao";

export interface Permissoes {
  /** False until the answer is known: nothing is hidden or shown on a guess. */
  carregado: boolean;
  pode: (permissao: string) => boolean;
}

/**
 * What the caller may do in the salão (GET /sessao/permissoes). It is only used to HIDE what the server would refuse: an action the person cannot do is absent, not disabled.
 * The server still decides every action; a 403 on any route is shown as "Você não tem permissão" without saying why.
 */
export function usePermissoes(): Permissoes {
  const sessao = useSessao();
  const unidade = sessao.status === "ativa" ? sessao.unidadeAtivaId : null;
  const consulta = useQuery({
    queryKey: ["permissoes", unidade],
    queryFn: async () => {
      const { data } = await api.GET("/sessao/permissoes");
      if (!data) {
        throw new Error("permissoes");
      }

      return new Set(data.permissoes);
    },
    enabled: unidade !== null,
    staleTime: 60_000,
  });
  return { carregado: consulta.isSuccess, pode: (permissao) => consulta.data?.has(permissao) ?? false };
}
