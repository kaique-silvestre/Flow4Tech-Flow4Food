import type { ContaDaComanda } from "./rodada";

/** The reasons offered for a discount, courtesy or gift. The server takes the text (1 to 200 characters); "Outro" asks for words of the person's own. */
export const MOTIVOS_DE_DESCONTO = [
  "Cliente frequente",
  "Reclamação do cliente",
  "Erro de atendimento",
  "Aniversário",
  "Decisão da gerência",
  "Outro",
] as const;

export type FormaDoDesconto = "percentual" | "valor";
export type AcaoDeDesconto = "desconto" | "cortesia" | "brinde";
export const ACOES_DE_DESCONTO: readonly AcaoDeDesconto[] = ["desconto", "cortesia", "brinde"];

export type PedidoDeDesconto =
  | { acao: "desconto"; itemId: string | null; forma: "percentual"; percentual: number; motivo: string }
  | { acao: "desconto"; itemId: string | null; forma: "valor"; valorCentavos: number; motivo: string }
  | { acao: "cortesia" | "brinde"; itemId: string; motivo: string };

/** What one discount takes off: the percent of the base (half up, the same rounding as the server) or the value typed, never more than the base. */
export function quantoTira(forma: FormaDoDesconto, numero: number, baseCentavos: number): number {
  const base = Math.max(0, baseCentavos);
  const pedido = forma === "percentual" ? Math.round((base * numero) / 100) : numero;
  return Math.min(Math.max(0, pedido), base);
}

/** A percent is a whole number from 1 to 100; a value is at least one cent. */
export function numeroValido(forma: FormaDoDesconto, numero: number): boolean {
  return forma === "percentual" ? Number.isInteger(numero) && numero >= 1 && numero <= 100 : numero >= 1;
}

export interface PreviaDaConta {
  descontoCentavos: number;
  taxaServicoCentavos: number;
  totalCentavos: number;
}

/**
 * The bill after taking `tiraCentavos` more: the order the server uses (ADR-018): consumption, minus discounts, service fee on what is left, half up. It starts from the server's own numbers
 * (consumption, discount so far, fee percent) and is only a preview: the answer of the server replaces it.
 */
export function previaDoTotal(conta: ContaDaComanda, tiraCentavos: number): PreviaDaConta {
  const descontoCentavos = (conta.descontoCentavos ?? 0) + tiraCentavos;
  const liquido = Math.max(0, conta.consumoCentavos - descontoCentavos);
  const taxaServicoCentavos = conta.taxaServicoRemovida
    ? 0
    : Math.round((liquido * (conta.taxaServicoPercentual ?? 0)) / 100);
  return { descontoCentavos, taxaServicoCentavos, totalCentavos: liquido + taxaServicoCentavos };
}

/** The sentence for an answer that did not apply. The limit is never named: a refusal says only that this value is not allowed. */
export function textoDaFalha(status: number, codigo: string | undefined): string {
  if (status === 403) {
    return "Sem permissão para este valor.";
  }

  if (status === 409 && codigo === "desconto-excede") {
    return "O desconto passa do que ainda falta cobrar. Use um valor menor.";
  }

  if (status === 409 && codigo === "valor-ja-recebido") {
    return "Já há valor recebido acima do novo total.";
  }

  if (status === 409 && codigo === "item-nao-normal") {
    return "Este item já é cortesia ou brinde.";
  }

  if (status === 409 && codigo === "item-ja-cancelado") {
    return "Este item já foi cancelado.";
  }

  if (status === 409 && codigo === "comanda-nao-aberta") {
    return "Esta comanda não está mais aberta.";
  }

  if (status === 409 && codigo === "versao-desatualizada") {
    return "Esta comanda foi alterada em outro aparelho. Atualizamos a conta; confira e aplique de novo.";
  }

  return "Não foi possível aplicar. Tente de novo.";
}
