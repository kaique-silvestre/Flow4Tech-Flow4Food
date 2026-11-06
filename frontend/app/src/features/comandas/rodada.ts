import type { Produto } from "./cardapio";

export interface ItemDaConta {
  id: string;
  rodadaId: string;
  produtoId: string;
  produtoNome: string;
  precoCentavos: number;
  quantidade: number;
  observacao: string | null;
  estado: "lancado" | "cancelado";
}

export interface ContaDaComanda {
  consumoCentavos: number;
  taxaServicoCentavos: number;
  totalCentavos: number;
  pagoCentavos: number;
  saldoCentavos: number;
  taxaServicoRemovida?: boolean;
  taxaServicoPercentual?: number;
  versao: number;
  estado: string;
}

export interface ItemEmPreparo {
  produto: Produto;
  quantidade: number;
}

export const QUANTIDADE_MAXIMA = 999;
export const ITENS_MAXIMOS_POR_RODADA = 50;

/** One more unit of a product in the round being prepared (the same product stays one line; at most 999 units, 50 lines). */
export function adicionar(emPreparo: readonly ItemEmPreparo[], produto: Produto): ItemEmPreparo[] {
  const existente = emPreparo.find((i) => i.produto.id === produto.id);
  if (existente) {
    return emPreparo.map((i) => (i === existente ? { ...i, quantidade: Math.min(QUANTIDADE_MAXIMA, i.quantidade + 1) } : i));
  }

  return emPreparo.length >= ITENS_MAXIMOS_POR_RODADA ? [...emPreparo] : [...emPreparo, { produto, quantidade: 1 }];
}

/** Sets the quantity of a line; zero (or less) removes it. */
export function definirQuantidade(emPreparo: readonly ItemEmPreparo[], produtoId: string, quantidade: number): ItemEmPreparo[] {
  if (quantidade <= 0) {
    return emPreparo.filter((i) => i.produto.id !== produtoId);
  }

  return emPreparo.map((i) => (i.produto.id === produtoId ? { ...i, quantidade: Math.min(QUANTIDADE_MAXIMA, quantidade) } : i));
}

export function unidades(emPreparo: readonly ItemEmPreparo[]): number {
  return emPreparo.reduce((soma, i) => soma + i.quantidade, 0);
}

export function totalEmPreparo(emPreparo: readonly ItemEmPreparo[]): number {
  return emPreparo.reduce((soma, i) => soma + i.produto.precoCentavos * i.quantidade, 0);
}

/** The items of the LAST round still standing (launched ones, in the order the server lists them): what "Repetir rodada" stages again. Null when there is no round. */
export function ultimaRodada(itens: readonly ItemDaConta[]): ItemDaConta[] | null {
  const lancados = itens.filter((i) => i.estado === "lancado");
  const ultimo = lancados.at(-1);
  if (!ultimo) {
    return null;
  }

  return lancados.filter((i) => i.rodadaId === ultimo.rodadaId);
}

/** The reasons of the standard list (docs: cliente desistiu, erro de lançamento, item indisponível, outro) with the code the server expects. */
export const MOTIVOS = [
  { codigo: "cliente-desistiu", rotulo: "Cliente desistiu" },
  { codigo: "erro-de-lancamento", rotulo: "Erro de lançamento" },
  { codigo: "item-indisponivel", rotulo: "Item indisponível" },
  { codigo: "outro", rotulo: "Outro" },
] as const;

export type CodigoDeMotivo = (typeof MOTIVOS)[number]["codigo"];

export const DURACAO_DO_DESFAZER_MS = 8000;
