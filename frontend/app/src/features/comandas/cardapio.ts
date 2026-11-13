import { useQuery } from "@tanstack/react-query";
import { aoEncerrarSessao, api } from "../sessao/sessao";

export interface Produto {
  id: string;
  categoriaId: string;
  codigo: string;
  nome: string;
  /** The price a waiter charges here and now (unit override > promotion in force > base). */
  precoCentavos: number;
  /** "Acabou": the server refuses to launch it, so the grid marks it and offers no add. */
  indisponivel?: boolean;
}

export interface Categoria {
  id: string;
  nome: string;
}

/** The grid shows what the server will charge (effective price), not the base price. */
export function paraProduto(p: Record<string, unknown>): Produto {
  return {
    id: String(p["id"]),
    categoriaId: String(p["categoriaId"]),
    codigo: String(p["codigo"]),
    nome: String(p["nome"]),
    precoCentavos: Number(p["precoEfetivoCentavos"] ?? p["precoCentavos"]),
    indisponivel: p["indisponivel"] === true,
  };
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

      return data
        .filter((c) => (c as { ativa?: boolean }).ativa !== false)
        .map((c) => ({ id: String(c.id), nome: String(c.nome) }));
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
  return texto
    .normalize("NFD")
    .replace(/\p{Diacritic}/gu, "")
    .toLowerCase();
}

/** Favorites of the signed-in waiter (what they launch most, here), best first. */
export function useFavoritos(enabled: boolean) {
  return useQuery<Produto[]>({
    queryKey: ["cardapio", "favoritos"],
    enabled,
    staleTime: 30_000,
    queryFn: async () => {
      const { data } = await api.GET("/cardapio/produtos/favoritos");
      if (!data) {
        throw new Error("favoritos");
      }

      return data.map((p) => paraProduto(p as unknown as Record<string, unknown>));
    },
  });
}

/** The longest search the server accepts (it answers 400 above this). */
export const BUSCA_MAXIMA = 80;

/**
 * The picker search, answered by the server (exact short code first, then names that start with the text, then names that contain it; accent- and case-insensitive; at most 20). The screen
 * shows the local filter of the loaded menu until this answers, and keeps it when there is no signal.
 */
export function useBusca(termo: string, enabled: boolean) {
  const limpo = termo.trim().slice(0, BUSCA_MAXIMA);
  return useQuery<Produto[]>({
    queryKey: ["cardapio", "busca", normalizar(limpo)],
    enabled: enabled && limpo !== "",
    staleTime: 30_000,
    queryFn: async () => {
      const { data } = await api.GET("/cardapio/produtos/busca", { params: { query: { q: limpo } } });
      if (!data) {
        throw new Error("busca");
      }

      return data.map((p) => paraProduto(p as unknown as Record<string, unknown>));
    },
  });
}

/** The same order the server uses, for the local filter: the exact short code, then names that start with the text, then the rest; the menu order breaks ties. */
function posicaoNaBusca(p: Produto, termo: string): number {
  if (normalizar(p.codigo) === termo) {
    return 0;
  }

  return normalizar(p.nome).startsWith(termo) ? 1 : 2;
}

/** The local search: part of the name or of the short code, ignoring case and accents, best match first. */
export function buscarProdutos(produtos: readonly Produto[], busca: string): Produto[] {
  const termo = normalizar(busca.trim());
  if (termo === "") {
    return [...produtos];
  }

  return produtos
    .filter((p) => normalizar(p.nome).includes(termo) || normalizar(p.codigo).includes(termo))
    .map((p, ordem) => ({ p, ordem, posicao: posicaoNaBusca(p, termo) }))
    .sort((a, b) => a.posicao - b.posicao || a.ordem - b.ordem)
    .map(({ p }) => p);
}

export interface Opcao {
  id: string;
  nome: string;
  precoCentavos: number;
}

export interface GrupoDeOpcao {
  id: string;
  nome: string;
  minimo: number;
  maximo: number;
  opcoes: Opcao[];
}

export interface OpcaoFrequente {
  grupoId: string;
  opcao: Opcao;
  lancamentos: number;
}

/** Only what a waiter can still pick: active groups with their active add-ons; a group with none left is dropped. */
function gruposDisponiveis(
  grupos: readonly {
    id: unknown;
    nome: unknown;
    minimo: unknown;
    maximo: unknown;
    ativo: boolean;
    opcoes: readonly { id: unknown; nome: unknown; precoCentavos: unknown; ativa: boolean }[];
  }[],
): GrupoDeOpcao[] {
  return grupos
    .filter((g) => g.ativo)
    .map((g) => ({
      id: String(g.id),
      nome: String(g.nome),
      minimo: Number(g.minimo),
      maximo: Number(g.maximo),
      opcoes: g.opcoes
        .filter((o) => o.ativa)
        .map((o) => ({ id: String(o.id), nome: String(o.nome), precoCentavos: Number(o.precoCentavos) })),
    }))
    .filter((g) => g.opcoes.length > 0);
}

export const GRUPOS_STALE_MS = 5 * 60_000;

export function chaveDosGrupos(produtoId: string) {
  return ["cardapio", "produto", produtoId, "grupos"] as const;
}

/** Last-known add-on groups per product, kept so a cold start without signal still knows which groups are required. Versioned; forgotten when the session ends (like the queue). */
const CHAVE_GRUPOS = "f4.grupos.v1";

function lerGruposGuardados(): Record<string, GrupoDeOpcao[]> {
  try {
    const bruto = localStorage.getItem(CHAVE_GRUPOS);
    return bruto ? (JSON.parse(bruto) as Record<string, GrupoDeOpcao[]>) : {};
  } catch {
    return {};
  }
}

function guardarGrupos(produtoId: string, grupos: GrupoDeOpcao[]) {
  try {
    localStorage.setItem(CHAVE_GRUPOS, JSON.stringify({ ...lerGruposGuardados(), [produtoId]: grupos }));
  } catch {
    // Storage full or blocked: the in-memory cache still works.
  }
}

aoEncerrarSessao(() => {
  try {
    localStorage.removeItem(CHAVE_GRUPOS);
  } catch {
    // Nothing to clear.
  }
});

/** Reads the groups; when the read fails, the last-known groups of that product stand in (an error only if there are none). */
export async function buscarGruposDoProduto(produtoId: string): Promise<GrupoDeOpcao[]> {
  try {
    const { data } = await api.GET("/cardapio/produtos/{id}/grupos-opcao", { params: { path: { id: produtoId } } });
    if (!data) {
      throw new Error("grupos");
    }

    const grupos = gruposDisponiveis(data);
    guardarGrupos(produtoId, grupos);
    return grupos;
  } catch (erro) {
    const guardados = lerGruposGuardados()[produtoId];
    if (guardados) {
      return guardados;
    }

    throw erro;
  }
}

/** The groups of one product (cached; the picker row reads them to offer "Opções", the tap reads them to decide whether the sheet opens). */
export function useGruposDoProduto(produtoId: string, enabled: boolean) {
  return useQuery<GrupoDeOpcao[]>({
    queryKey: chaveDosGrupos(produtoId),
    enabled,
    staleTime: GRUPOS_STALE_MS,
    queryFn: () => buscarGruposDoProduto(produtoId),
  });
}

export function useOpcoesFrequentes(produtoId: string | null, enabled: boolean) {
  return useQuery<OpcaoFrequente[]>({
    queryKey: ["cardapio", "produto", produtoId, "opcoes-frequentes"],
    enabled: enabled && produtoId !== null,
    staleTime: 30_000,
    queryFn: async () => {
      const { data } = await api.GET("/cardapio/produtos/{id}/opcoes-frequentes", {
        params: { path: { id: produtoId ?? "" } },
      });
      if (!data) {
        throw new Error("opcoes-frequentes");
      }

      return data.map((f) => ({
        grupoId: String(f.grupoId),
        lancamentos: Number(f.lancamentos),
        opcao: { id: String(f.opcao.id), nome: String(f.opcao.nome), precoCentavos: Number(f.opcao.precoCentavos) },
      }));
    },
  });
}
