export type AcaoDeMovimento = "transferir" | "separar" | "juntar";

/** The failure body the three movement routes answer (`MovimentoErro`): the code, which comanda a stale version belongs to, and the version now. */
export interface FalhaDeMovimento {
  codigo?: string;
  comandaId?: string | null;
  versaoAtual?: number | null;
}

const PERMISSAO: Record<AcaoDeMovimento, string> = {
  transferir: "transferir itens",
  separar: "separar itens",
  juntar: "juntar comandas",
};

/** Toggles one id of a selection (a new set, the old one is never changed). */
export function alternarSelecao(atual: ReadonlySet<string>, id: string): Set<string> {
  const novo = new Set(atual);
  if (!novo.delete(id)) {
    novo.add(id);
  }

  return novo;
}

/** The sentence for a movement the server refused. A 403 never says why (and never names a limit); a 409 says what to do, and the screen keeps the selection. */
export function textoDoMovimento(acao: AcaoDeMovimento, status: number, codigo: string | undefined): string {
  if (status === 403) {
    return `Você não tem permissão para ${PERMISSAO[acao]}.`;
  }

  if (status === 404) {
    return "Uma das comandas não está mais disponível para você. Atualizamos a lista.";
  }

  if (status === 409) {
    switch (codigo) {
      case "versao-desatualizada":
        return "Uma das comandas foi alterada em outro aparelho. Atualizamos os números; confira e confirme de novo.";
      case "comanda-nao-aberta":
        return "Uma das comandas não está mais aberta.";
      case "item-ja-cancelado":
        return "Um dos itens já foi cancelado. Atualizamos a conta; confira a seleção.";
      case "item-com-desconto":
        return "Um dos itens tem desconto e não pode sair desta comanda. Tire-o da seleção.";
      case "comanda-com-desconto":
        return "A comanda escolhida tem desconto e não pode ser juntada.";
      case "valor-ja-recebido":
        return acao === "juntar"
          ? "A comanda escolhida já tem pagamento e não pode ser juntada."
          : "O que já foi recebido não cabe no que sobraria da conta. Escolha menos itens.";
      default:
        break;
    }
  }

  if (status === 400) {
    return "Confira a seleção e tente de novo.";
  }

  return "Não foi possível concluir. Tente de novo.";
}

/** Why the sheet cannot confirm right now (no signal, actions of the comanda waiting in the queue); null when it can. These operations are never queued. */
export function bloqueioDaOperacao(online: boolean, filaDaComanda: boolean, oQue: string): string | null {
  if (!online) {
    return `Sem conexão. ${oQue} precisa do servidor e não fica na fila.`;
  }

  return filaDaComanda ? "Há ações desta comanda na fila. Faça isto depois que forem enviadas." : null;
}
