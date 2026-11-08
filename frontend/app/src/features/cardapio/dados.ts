import { api } from "../sessao/sessao";
import { type CategoriaGestao, type ProdutoGestao, paraCategoriaGestao, paraProdutoGestao } from "./logica";

/** A refusal by right is a bare 403: the screen says "sem permissão" and never why. */
export class Recusado extends Error {}

export interface DadosDaGestao {
  produtos: ProdutoGestao[];
  categorias: CategoriaGestao[];
}

/** Every key the management screen caches lives under this prefix, apart from the waiter's picker (["cardapio","produtos"], ...), which has other shapes. */
export const CHAVE_DA_GESTAO = ["cardapio", "gestao"] as const;

export async function carregarGestao(): Promise<DadosDaGestao> {
  const [produtos, categorias] = await Promise.all([
    api.GET("/cardapio/produtos/gestao"),
    api.GET("/cardapio/categorias"),
  ]);
  if (produtos.response.status === 403) {
    throw new Recusado();
  }

  if (!produtos.data || !categorias.data) {
    throw new Error("cardapio");
  }

  return { produtos: produtos.data.map(paraProdutoGestao), categorias: categorias.data.map(paraCategoriaGestao) };
}
