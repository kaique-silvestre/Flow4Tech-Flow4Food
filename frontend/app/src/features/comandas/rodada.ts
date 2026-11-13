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
  /** Add-ons the server froze on this item (name and price at launch time). */
  opcoes: OpcaoEscolhida[];
  /** Price of ONE unit with its add-ons: what the server multiplies by the quantity. */
  unitarioCentavos: number;
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

/** An add-on picked for a staged item. The screen shows its price; the server recomputes name and price from the id. */
export interface OpcaoEscolhida {
  id: string;
  grupoId: string;
  nome: string;
  precoCentavos: number;
}

export interface ItemEmPreparo {
  /** Identity of the line: the product alone for a plain item, else product + add-ons + note (the same combination stays one line). */
  chave: string;
  produto: Produto;
  quantidade: number;
  opcoes: OpcaoEscolhida[];
  observacao: string | null;
}

export const QUANTIDADE_MAXIMA = 999;
const ITENS_MAXIMOS_POR_RODADA = 50;
export const OBSERVACAO_MAXIMA = 200;

/** The note as the server stores it: trimmed, no control characters, null when blank. */
export function normalizarObservacao(texto: string | null | undefined): string | null {
  // biome-ignore lint/suspicious/noControlCharactersInRegex: the server refuses control characters in a note, so they are dropped here.
  const limpo = (texto ?? "").replace(/[\u0000-\u001f\u007f]/g, " ").trim();
  return limpo === "" ? null : limpo.slice(0, OBSERVACAO_MAXIMA);
}

function chaveDoItem(produtoId: string, opcoes: readonly OpcaoEscolhida[], observacao: string | null): string {
  if (opcoes.length === 0 && observacao === null) {
    return produtoId;
  }

  return `${produtoId}|${opcoes
    .map((o) => o.id)
    .sort()
    .join(",")}|${observacao ?? ""}`;
}

/** Price of one unit of a staged item: the product plus its add-ons (the same sum the server freezes). */
export function precoUnitario(item: Pick<ItemEmPreparo, "produto" | "opcoes">): number {
  return item.produto.precoCentavos + item.opcoes.reduce((soma, o) => soma + o.precoCentavos, 0);
}

/** One more unit of a plain product in the round being prepared (the same product stays one line; at most 999 units, 50 lines). */
export function adicionar(emPreparo: readonly ItemEmPreparo[], produto: Produto): ItemEmPreparo[] {
  return adicionarConfigurado(emPreparo, { produto, quantidade: 1, opcoes: [], observacao: null });
}

export interface Configuracao {
  produto: Produto;
  quantidade: number;
  opcoes: OpcaoEscolhida[];
  observacao: string | null;
}

/** Adds an item with its add-ons and note; an identical line gets the units added, a different combination is a new line. */
export function adicionarConfigurado(emPreparo: readonly ItemEmPreparo[], c: Configuracao): ItemEmPreparo[] {
  const observacao = normalizarObservacao(c.observacao);
  const chave = chaveDoItem(c.produto.id, c.opcoes, observacao);
  const existente = emPreparo.find((i) => i.chave === chave);
  if (existente) {
    return emPreparo.map((i) =>
      i === existente ? { ...i, quantidade: Math.min(QUANTIDADE_MAXIMA, i.quantidade + c.quantidade) } : i,
    );
  }

  if (emPreparo.length >= ITENS_MAXIMOS_POR_RODADA) {
    return [...emPreparo];
  }

  return [
    ...emPreparo,
    {
      chave,
      produto: c.produto,
      quantidade: Math.min(QUANTIDADE_MAXIMA, Math.max(1, c.quantidade)),
      opcoes: c.opcoes,
      observacao,
    },
  ];
}

/** Replaces a staged line by a new configuration (edit); if it now equals another line, the two merge. */
export function substituirItem(emPreparo: readonly ItemEmPreparo[], chave: string, c: Configuracao): ItemEmPreparo[] {
  const posicao = emPreparo.findIndex((i) => i.chave === chave);
  if (posicao < 0) {
    return [...emPreparo];
  }

  const semOriginal = emPreparo.filter((i) => i.chave !== chave);
  const novo = adicionarConfigurado([], c);
  const outro = semOriginal.find((i) => i.chave === novo[0]?.chave);
  if (outro) {
    return semOriginal.map((i) =>
      i === outro ? { ...i, quantidade: Math.min(QUANTIDADE_MAXIMA, i.quantidade + (novo[0]?.quantidade ?? 0)) } : i,
    );
  }

  return [...semOriginal.slice(0, posicao), ...novo, ...semOriginal.slice(posicao)];
}

/** Sets the quantity of a line; zero (or less) removes it. */
export function definirQuantidade(
  emPreparo: readonly ItemEmPreparo[],
  chave: string,
  quantidade: number,
): ItemEmPreparo[] {
  if (quantidade <= 0) {
    return emPreparo.filter((i) => i.chave !== chave);
  }

  return emPreparo.map((i) => (i.chave === chave ? { ...i, quantidade: Math.min(QUANTIDADE_MAXIMA, quantidade) } : i));
}

export function unidades(emPreparo: readonly ItemEmPreparo[]): number {
  return emPreparo.reduce((soma, i) => soma + i.quantidade, 0);
}

/** What the server will total for this round: (product + add-ons) x quantity per line. */
export function totalEmPreparo(emPreparo: readonly ItemEmPreparo[]): number {
  return emPreparo.reduce((soma, i) => soma + precoUnitario(i) * i.quantidade, 0);
}

/** Wire form of the staged items (add-ons by id only; the server recomputes names and prices). */
export function paraItensDaRodada(emPreparo: readonly ItemEmPreparo[]) {
  return emPreparo.map((i) => ({
    produtoId: i.produto.id,
    quantidade: i.quantidade,
    observacao: i.observacao,
    ...(i.opcoes.length > 0 ? { opcaoIds: i.opcoes.map((o) => o.id) } : {}),
  }));
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
