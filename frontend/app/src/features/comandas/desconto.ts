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

/**
 * What one discount takes off: the percent of the base (half up, the same rounding as the server) or the value typed, never more than the base. The percent may have two decimals; it is taken
 * in hundredths of a percent and the half is found with integers, so 0,5% of R$ 1,00 never goes down by a float error.
 */
export function quantoTira(forma: FormaDoDesconto, numero: number, baseCentavos: number): number {
  const base = Math.max(0, baseCentavos);
  const pedido = forma === "percentual" ? Math.floor((base * Math.round(numero * 100) + 5000) / 10_000) : numero;
  return Math.min(Math.max(0, pedido), base);
}

/** A percent is above 0 and up to 100 with at most two decimals; a value is at least one cent. */
export function numeroValido(forma: FormaDoDesconto, numero: number): boolean {
  return forma === "percentual"
    ? numero > 0 && numero <= 100 && Math.round(numero * 100) / 100 === numero
    : numero >= 1;
}

/**
 * The percent being typed, as a string with "." (what `Number` reads and the request carries). Up to three digits before the separator and two after; "," or "." starts the decimals once
 * ("0," when nothing was typed); a leading zero never stays ("05" is "5").
 */
export function digitarPercentual(atual: string, tecla: string): string {
  if (tecla === "," || tecla === ".") {
    return atual.includes(".") ? atual : `${atual === "" ? "0" : atual}.`;
  }

  const [inteiro = "", decimais] = atual.split(".");
  if (decimais !== undefined) {
    return decimais.length >= 2 ? atual : `${atual}${tecla}`;
  }

  return `${inteiro}${tecla}`.replace(/^0+/, "").slice(0, 3);
}

/** The percent as shown on the keypad: decimal comma, "0" while nothing was typed. */
export function percentualNaTela(digitos: string): string {
  return `${digitos === "" ? "0" : digitos.replace(".", ",")}%`;
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
