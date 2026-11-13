import type { components } from "@flow4tech/api-client";
import { formatBRL } from "@flow4tech/design-system";
import type { ItemDaConta } from "./rodada";

export type ModoDaDivisao = "iguais" | "assento" | "item" | "valores" | "percentual";

export const MODOS_DA_DIVISAO: readonly { modo: ModoDaDivisao; rotulo: string }[] = [
  { modo: "iguais", rotulo: "Iguais" },
  { modo: "assento", rotulo: "Por assento" },
  { modo: "item", rotulo: "Por item" },
  { modo: "valores", rotulo: "Valores livres" },
  { modo: "percentual", rotulo: "Percentual" },
];

export const PARTES_MINIMO = 2;
export const PARTES_MAXIMO = 50;

type PedidoDeDivisao = components["schemas"]["PedidoDeDivisaoRequest"];

/** What the person is typing in the split sheet: one set of fields per mode, all kept while the mode changes so nothing typed is lost by looking at another way. */
export interface FormularioDaDivisao {
  modo: ModoDaDivisao;
  partes: number;
  /** Item mode: the part (1-based) each launched item goes to. */
  grupoPorItem: Record<string, number>;
  /** Free-values mode: cents per part, as digits (what MoneyField edits). */
  valores: string[];
  /** Percent mode: the text typed per part (comma or dot, two decimals). */
  percentuais: string[];
}

/** Total shared into `n` equal slices: the floor each, the leftover cents one by one from the first (the server's rule, so the starting values already add up). */
function fatiasIguais(total: number, n: number): number[] {
  const base = Math.floor(total / n);
  const resto = total - base * n;
  return Array.from({ length: n }, (_, i) => base + (i < resto ? 1 : 0));
}

function reiniciar(partes: number, itens: readonly ItemDaConta[], totalCentavos: number) {
  return {
    partes,
    grupoPorItem: Object.fromEntries(itens.map((item, i) => [item.id, (i % partes) + 1])),
    valores: fatiasIguais(Math.max(0, totalCentavos), partes).map(String),
    percentuais: fatiasIguais(10_000, partes).map((c) => (c / 100).toFixed(2).replace(/\.?0+$/, "")),
  };
}

export function formularioInicial(itens: readonly ItemDaConta[], totalCentavos: number): FormularioDaDivisao {
  return { modo: "iguais", ...reiniciar(PARTES_MINIMO, itens, totalCentavos) };
}

/** A new number of parts (kept between 2 and 50). Values and percents restart as an even split; an item stays on its part while that part still exists. */
export function mudarPartes(
  f: FormularioDaDivisao,
  partes: number,
  itens: readonly ItemDaConta[],
  totalCentavos: number,
): FormularioDaDivisao {
  const n = Math.min(PARTES_MAXIMO, Math.max(PARTES_MINIMO, partes));
  const novo = reiniciar(n, itens, totalCentavos);
  return {
    ...f,
    ...novo,
    grupoPorItem: Object.fromEntries(
      itens.map((item) => [
        item.id,
        f.grupoPorItem[item.id] === undefined
          ? (novo.grupoPorItem[item.id] ?? 1)
          : (((f.grupoPorItem[item.id] as number) - 1) % n) + 1,
      ]),
    ),
  };
}

/** A percent in hundredths (33,5 is 3350); "" is zero; null when it is not a percent from 0 to 100 with at most two decimals. */
export function centesimosDoPercentual(texto: string): number | null {
  if (texto === "") {
    return 0;
  }

  if (!/^\d{1,3}([.,]\d{0,2})?$/.test(texto)) {
    return null;
  }

  const centesimos = Math.round(Number(texto.replace(",", ".")) * 100);
  return centesimos > 10_000 ? null : centesimos;
}

/** What the field keeps of what was typed: digits and one separator (the first one typed), up to three before it and two after. */
export function digitarPercentual(texto: string): string {
  const limpo = texto.replace(/[^\d.,]/g, "");
  const posicao = limpo.search(/[.,]/);
  if (posicao < 0) {
    return limpo.slice(0, 3);
  }

  const inteiro = limpo.slice(0, posicao).slice(0, 3);
  const decimais = limpo
    .slice(posicao + 1)
    .replace(/[.,]/g, "")
    .slice(0, 2);
  return `${inteiro}${limpo[posicao]}${decimais}`;
}

export type PedidoMontado = { ok: true; pedido: PedidoDeDivisao } | { ok: false; aviso: string | null };

const VAZIO = { partes: null, grupos: null, valores: null, percentuais: null, gorjetaCentavos: 0 } as const;

/**
 * The request for the server's preview and for confirming, from what is typed. When the fields cannot make a valid request (a part with no item, values that do not add up) it says why in
 * words and no request goes out: the server is asked only about something it can answer.
 */
export function pedidoDaDivisao(
  f: FormularioDaDivisao,
  itens: readonly ItemDaConta[],
  totalCentavos: number,
): PedidoMontado {
  switch (f.modo) {
    case "iguais":
      return { ok: true, pedido: { ...VAZIO, modo: "iguais", partes: f.partes } };
    case "assento":
      return { ok: true, pedido: { ...VAZIO, modo: "assento" } };
    case "item": {
      const grupos = Array.from({ length: f.partes }, (_, k) =>
        itens.filter((i) => f.grupoPorItem[i.id] === k + 1).map((i) => i.id),
      );
      return grupos.some((g) => g.length === 0)
        ? { ok: false, aviso: "Cada parte precisa de pelo menos um item. Reduza as partes ou mude os itens de parte." }
        : { ok: true, pedido: { ...VAZIO, modo: "item", grupos } };
    }
    case "valores": {
      const valores = f.valores.map((v) => (v === "" ? 0 : Number(v)));
      const diferenca = totalCentavos - valores.reduce((soma, v) => soma + v, 0);
      if (diferenca !== 0) {
        return {
          ok: false,
          aviso:
            diferenca > 0
              ? `Faltam ${formatBRL(diferenca)} para fechar a conta de ${formatBRL(totalCentavos)}.`
              : `Passou ${formatBRL(-diferenca)} da conta de ${formatBRL(totalCentavos)}.`,
        };
      }

      return { ok: true, pedido: { ...VAZIO, modo: "valores", valores } };
    }
    case "percentual": {
      const centesimos = f.percentuais.map(centesimosDoPercentual);
      if (centesimos.some((c) => c === null)) {
        return { ok: false, aviso: "Confira os percentuais: de 0 a 100, com até duas casas." };
      }

      const soma = (centesimos as number[]).reduce((s, c) => s + c, 0);
      if (soma !== 10_000) {
        return {
          ok: false,
          aviso: `A soma dos percentuais é ${(soma / 100).toString().replace(".", ",")}% e precisa ser 100%.`,
        };
      }

      return {
        ok: true,
        pedido: { ...VAZIO, modo: "percentual", percentuais: (centesimos as number[]).map((c) => c / 100) },
      };
    }
  }
}

/** The sentence for a split the server refused (or could not preview). A 403 never says why. */
export function textoDaFalhaDaDivisao(status: number, codigo: string | undefined, modo: ModoDaDivisao): string {
  if (status === 403) {
    return "Você não tem permissão para dividir a conta.";
  }

  if (status === 404) {
    return "Esta comanda não está mais disponível para você.";
  }

  if (status === 400) {
    if (codigo === "soma-diferente") {
      return modo === "percentual"
        ? "Os percentuais precisam somar 100%."
        : "Os valores precisam somar o total da conta.";
    }

    if (codigo === "itens-fora-das-partes") {
      return "Todo item precisa estar em uma parte.";
    }

    return modo === "assento"
      ? "Para dividir por assento, ao menos dois assentos precisam ter itens marcados no lançamento. Use Por item para escolher quem paga o quê."
      : "Confira os dados da divisão.";
  }

  if (status === 409) {
    switch (codigo) {
      case "versao-desatualizada":
        return "Esta comanda foi alterada em outro aparelho. Atualizamos a conta e a prévia; confira e confirme de novo.";
      case "comanda-nao-aberta":
        return "Esta comanda não está mais aberta.";
      case "valor-ja-recebido":
        return "Esta comanda já recebeu pagamento e não pode ser dividida.";
      default:
        break;
    }
  }

  return "Não foi possível dividir a conta. Tente de novo.";
}
