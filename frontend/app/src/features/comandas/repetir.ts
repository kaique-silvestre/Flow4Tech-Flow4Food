import type { Produto } from "./cardapio";
import type { ItemDaConta } from "./rodada";

/** A line of the last round the preview shows, with the price the menu charges now (the server rereads it too). */
export interface LinhaDaPrevia {
  item: ItemDaConta;
  /** One unit with its add-ons, at the menu price of now. */
  unitarioCentavos: number;
}

export interface PreviaDaRepeticao {
  entram: LinhaDaPrevia[];
  /** Lines the screen already knows will not go (the product is "acabou" or gone from the menu). Add-ons turned off are only known by the server and come back in its answer. */
  naoEntram: ItemDaConta[];
  totalCentavos: number;
}

/** What "Repetir última rodada" would send, from what the screen already holds: the live items of the last round against the menu now. */
export function previaDaRepeticao(ultima: readonly ItemDaConta[], produtos: readonly Produto[]): PreviaDaRepeticao {
  const entram: LinhaDaPrevia[] = [];
  const naoEntram: ItemDaConta[] = [];
  for (const item of ultima) {
    const produto = produtos.find((p) => p.id === item.produtoId);
    if (!produto || produto.indisponivel) {
      naoEntram.push(item);
      continue;
    }

    entram.push({
      item,
      unitarioCentavos: produto.precoCentavos + item.opcoes.reduce((s, o) => s + o.precoCentavos, 0),
    });
  }

  return {
    entram,
    naoEntram,
    totalCentavos: entram.reduce((s, l) => s + l.unitarioCentavos * l.item.quantidade, 0),
  };
}

export interface ItemIgnorado {
  produtoNome: string;
  quantidade: number;
  motivo: string;
}

/** Why the server left an item out, in the words the floor uses. */
export function motivoDoIgnorado(motivo: string): string {
  switch (motivo) {
    case "produto-inativo":
      return "acabou ou saiu do cardápio";
    case "opcao-inativa":
      return "um adicional não está disponível";
    case "opcao-invalida":
      return "os adicionais mudaram";
    default:
      return "não pôde ser repetido";
  }
}

/** "Não entraram: 1× Chopp (acabou ou saiu do cardápio); 2× Pastel (um adicional não está disponível)". Empty when nothing was left out. */
export function textoDosIgnorados(ignorados: readonly ItemIgnorado[]): string {
  if (ignorados.length === 0) {
    return "";
  }

  return `Não entraram: ${ignorados.map((i) => `${i.quantidade}× ${i.produtoNome} (${motivoDoIgnorado(i.motivo)})`).join("; ")}.`;
}

/** The ignored list of a server answer, typed loosely (numbers may arrive as strings). */
export function lerIgnorados(
  lista: readonly { produtoNome: string; quantidade: number | string; motivo: string }[] | undefined,
): ItemIgnorado[] {
  return (lista ?? []).map((i) => ({
    produtoNome: String(i.produtoNome),
    quantidade: Number(i.quantidade),
    motivo: String(i.motivo),
  }));
}

/** "hoje", "ontem", "há 3 dias" or the date: when the last bill of a table closed. */
export function quandoFechou(fechadaEm: string | null, agora: number): string {
  if (!fechadaEm) {
    return "";
  }

  const quando = new Date(fechadaEm);
  if (Number.isNaN(quando.getTime())) {
    return "";
  }

  const hoje = new Date(agora);
  const meiaNoite = (d: Date) => new Date(d.getFullYear(), d.getMonth(), d.getDate()).getTime();
  const dias = Math.round((meiaNoite(hoje) - meiaNoite(quando)) / 86_400_000);
  if (dias <= 0) {
    return "hoje";
  }

  if (dias === 1) {
    return "ontem";
  }

  if (dias < 7) {
    return `há ${dias} dias`;
  }

  return quando.toLocaleDateString("pt-BR", { day: "2-digit", month: "2-digit" });
}
