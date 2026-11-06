import { useQuery } from "@tanstack/react-query";
import { api } from "../sessao/sessao";

export interface Produto {
  id: string;
  categoriaId: string;
  codigo: string;
  nome: string;
  precoCentavos: number;
}

export interface Categoria {
  id: string;
  nome: string;
}

function paraProduto(p: Record<string, unknown>): Produto {
  return { id: String(p["id"]), categoriaId: String(p["categoriaId"]), codigo: String(p["codigo"]), nome: String(p["nome"]), precoCentavos: Number(p["precoCentavos"]) };
}

export function useProdutos(enabled: boolean) {
  return useQuery<Produto[]>({
    queryKey: ["cardapio", "produtos"],
    enabled,
    staleTime: 60_000,
    queryFn: async () => {
      const { data } = await api.GET("/cardapio/produtos");
      if (!data) {
        throw new Error("produtos");
      }

      return data.map((p) => paraProduto(p as unknown as Record<string, unknown>));
    },
  });
}

export function useCategorias(enabled: boolean) {
  return useQuery<Categoria[]>({
    queryKey: ["cardapio", "categorias"],
    enabled,
    staleTime: 60_000,
    queryFn: async () => {
      const { data } = await api.GET("/cardapio/categorias");
      if (!data) {
        throw new Error("categorias");
      }

      return data.filter((c) => (c as { ativa?: boolean }).ativa !== false).map((c) => ({ id: String(c.id), nome: String(c.nome) }));
    },
  });
}

export function useFrequentes(enabled: boolean) {
  return useQuery<Produto[]>({
    queryKey: ["cardapio", "frequentes"],
    enabled,
    staleTime: 30_000,
    queryFn: async () => {
      const { data } = await api.GET("/cardapio/produtos/frequentes");
      if (!data) {
        throw new Error("frequentes");
      }

      return data.map((p) => paraProduto(p as unknown as Record<string, unknown>));
    },
  });
}

function normalizar(texto: string): string {
  return texto.normalize("NFD").replace(/\p{Diacritic}/gu, "").toLowerCase();
}

/** The picker search: part of the name or of the short code, ignoring case and accents. */
export function buscarProdutos(produtos: readonly Produto[], busca: string): Produto[] {
  const termo = normalizar(busca.trim());
  if (termo === "") {
    return [...produtos];
  }

  return produtos.filter((p) => normalizar(p.nome).includes(termo) || normalizar(p.codigo).includes(termo));
}
