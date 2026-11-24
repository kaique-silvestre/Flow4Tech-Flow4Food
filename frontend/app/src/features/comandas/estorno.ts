export const MOTIVOS_DE_ESTORNO = [
  "Erro de digitação",
  "Cobrança em dobro",
  "Reclamação do cliente",
  "Produto devolvido",
  "Decisão da gerência",
  "Outro",
] as const;

export const LIMITE_DO_MOTIVO = 200;

export type FormaDoEstorno = "total" | "parcial";

/** A payment as the estorno window needs it: what it applied and what is still refundable (never less than zero). */
export interface PagamentoEstornavel {
  id: string;
  metodoNome: string;
  aplicadoCentavos: number;
  estornadoCentavos: number;
  saldoEstornavelCentavos: number;
  registradoEm: string;
}

/** Only a confirmed payment can be refunded; the server answers 409 for any other. */
export function estornavel(pagamento: { estado: string }): boolean {
  return pagamento.estado === "confirmado";
}

/** What the refund will take back: the whole refundable balance, or the typed cents. */
export function valorDoEstorno(forma: FormaDoEstorno, digitos: string, saldoEstornavelCentavos: number): number {
  return forma === "total" ? saldoEstornavelCentavos : digitos === "" ? 0 : Number(digitos);
}

/** A partial refund is at least one cent and at most the refundable balance. */
export function valorValido(forma: FormaDoEstorno, digitos: string, saldoEstornavelCentavos: number): boolean {
  const valor = valorDoEstorno(forma, digitos, saldoEstornavelCentavos);
  return saldoEstornavelCentavos >= 1 && valor >= 1 && valor <= saldoEstornavelCentavos;
}

/** The reason that goes in the request: the chip, or the typed text for "Outro"; empty while the person has not finished. */
export function motivoFinal(motivo: string | null, texto: string): string {
  const escolhido = motivo === "Outro" ? texto.trim() : (motivo ?? "");
  return escolhido.slice(0, LIMITE_DO_MOTIVO);
}

/** The request body: no value means the total (the server computes it), a value means a partial refund. Limit and shift are never sent. */
export function corpoDoEstorno(
  forma: FormaDoEstorno,
  digitos: string,
  motivo: string,
): { valorCentavos: number | null; motivo: string } {
  return { valorCentavos: forma === "total" ? null : Number(digitos), motivo };
}

/** The sentence for a refund the server refused. A 403 never says why or what the limit is. */
export function textoDoEstorno(status: number, codigo: string | undefined): string {
  if (status === 403) {
    return "Você não tem permissão para estornar este valor.";
  }

  switch (codigo) {
    case "excede-saldo-estornavel":
      return "O valor passa do que ainda pode ser estornado. Atualizamos; confira e tente de novo.";
    case "sem-turno-aberto":
      return "Não há turno de caixa aberto. Abra o caixa para estornar.";
    case "caixa-sem-dinheiro":
      return "Não há dinheiro suficiente no caixa para devolver este valor.";
    case "pagamento-nao-confirmado":
      return "Este pagamento ainda não foi confirmado e não pode ser estornado.";
    case "nao-encontrado":
      return "Não encontramos este pagamento. Atualizamos a lista.";
    default:
      return status >= 500 || status === 0
        ? "Não foi possível estornar. Tente de novo."
        : "O estorno não foi aceito. Confira o valor e o motivo.";
  }
}
