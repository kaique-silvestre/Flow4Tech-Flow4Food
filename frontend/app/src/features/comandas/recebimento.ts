import { centsFromDigits } from "@flow4tech/design-system";

/** A method the person can take money with: the ACTIVE ones of `GET /pagamentos/metodos` (no list lives in the code). */
export interface MetodoDeRecebimento {
  id: string;
  nome: string;
  dinheiro: boolean;
}

/** One line of the receipt: one method, once. `digitos` empty means "what is left" (the first such line takes it). */
export interface LinhaDeRecebimento {
  /** Made on the client when the line is added and kept while it stays: a retry after a lost answer is a repeat, never a second payment. */
  pagamentoId: string;
  metodoId: string;
  digitos: string;
}

export type ProblemaDoRecebimento =
  | { tipo: "sem-valor"; metodoId: string }
  | { tipo: "excede" }
  | { tipo: "dinheiro-sem-saldo"; metodoId: string };

export interface CalculoDoRecebimento {
  /** The amount of each line, in the order of the lines (the empty "rest" line already filled in). */
  valores: number[];
  /** What the methods that are not cash bring: applied exactly. */
  naoDinheiroCentavos: number;
  /** The cash handed over (change included). */
  dinheiroRecebidoCentavos: number;
  /** What the cash really pays (never past what the other methods left). */
  dinheiroAplicadoCentavos: number;
  /** Change: only cash gives it. */
  trocoCentavos: number;
  /** What is received for the bill now (no change, no tip). */
  aplicadoCentavos: number;
  /** What stays open on the target after this receipt. */
  faltaCentavos: number;
  problema: ProblemaDoRecebimento | null;
}

/**
 * The same rule the server applies (`Ratear`, docs/agents/salao.md): methods that are not cash apply exactly what they bring and together may not pass the target; cash takes what is left
 * and the surplus is change; every line applies at least one cent. The screen mirrors it only to show the balance live and to block the button; the server decides.
 */
export function calcularRecebimento(
  linhas: readonly LinhaDeRecebimento[],
  metodos: readonly MetodoDeRecebimento[],
  alvoCentavos: number,
): CalculoDoRecebimento {
  const ehDinheiro = (l: LinhaDeRecebimento) => metodos.find((m) => m.id === l.metodoId)?.dinheiro ?? false;
  const digitadas = linhas.map((l) => (l.digitos === "" ? null : centsFromDigits(l.digitos)));
  const somaDigitadas = digitadas.reduce<number>((soma, v) => soma + (v ?? 0), 0);
  const primeiraVazia = digitadas.indexOf(null);
  const valores = digitadas.map((v, i) => v ?? (i === primeiraVazia ? Math.max(0, alvoCentavos - somaDigitadas) : 0));

  let naoDinheiro = 0;
  let dinheiroRecebido = 0;
  linhas.forEach((l, i) => {
    if (ehDinheiro(l)) {
      dinheiroRecebido += valores[i] ?? 0;
    } else {
      naoDinheiro += valores[i] ?? 0;
    }
  });

  const sobra = Math.max(0, alvoCentavos - naoDinheiro);
  const dinheiroAplicado = Math.min(dinheiroRecebido, sobra);
  const aplicado = naoDinheiro + dinheiroAplicado;
  let problema: ProblemaDoRecebimento | null = null;
  const zerada = linhas.findIndex((_, i) => (valores[i] ?? 0) <= 0);
  if (zerada >= 0) {
    problema = { tipo: "sem-valor", metodoId: linhas[zerada]?.metodoId ?? "" };
  } else if (naoDinheiro > alvoCentavos) {
    problema = { tipo: "excede" };
  } else {
    const dinheiro = linhas.find(ehDinheiro);
    if (dinheiro && dinheiroAplicado <= 0) {
      problema = { tipo: "dinheiro-sem-saldo", metodoId: dinheiro.metodoId };
    }
  }

  return {
    valores,
    naoDinheiroCentavos: naoDinheiro,
    dinheiroRecebidoCentavos: dinheiroRecebido,
    dinheiroAplicadoCentavos: dinheiroAplicado,
    trocoCentavos: dinheiroRecebido - dinheiroAplicado,
    aplicadoCentavos: aplicado,
    faltaCentavos: Math.max(0, alvoCentavos - aplicado),
    problema,
  };
}

/**
 * Toggles a method. A method already in the receipt leaves it; a new one is added (several lines, only with `pagamento:dividir`) or replaces the only line (one line, without it).
 * A new line starts empty: it is the one the keypad types in next.
 */
export function alternarMetodo(
  linhas: readonly LinhaDeRecebimento[],
  metodoId: string,
  varias: boolean,
  novoPagamentoId: string,
): { linhas: LinhaDeRecebimento[]; ativo: string | null } {
  if (linhas.some((l) => l.metodoId === metodoId)) {
    const restantes = linhas.filter((l) => l.metodoId !== metodoId);
    return { linhas: restantes, ativo: restantes.at(-1)?.metodoId ?? null };
  }

  const nova: LinhaDeRecebimento = { pagamentoId: novoPagamentoId, metodoId, digitos: "" };
  return { linhas: varias ? [...linhas, nova] : [nova], ativo: metodoId };
}

const DIGITOS_MAXIMO = 9;

/** One more digit on an amount typed as cents (the cents fill from the right, like the cash register); a leading zero is dropped. */
export function digitarValor(digitos: string, digito: string): string {
  return `${digitos}${digito}`.replace(/^0+(?=\d)/, "").slice(0, DIGITOS_MAXIMO);
}

export function apagarValor(digitos: string): string {
  return digitos.slice(0, -1);
}

export interface ParteParaReceber {
  numero: number;
  assento: number | null;
  totalCentavos: number;
  /** What this part still owes: its total minus what was applied to it (all of it when the payments are not known). */
  faltaCentavos: number;
}

interface ParteDoServidor {
  numero: number | string;
  assento: null | number | string;
  totalCentavos: number | string;
}

interface PagamentoDoServidor {
  divisaoId: null | string;
  divisaoParte: null | number | string;
  aplicadoCentavos: number | string;
  estado: string;
}

/** The parts of the current split with what each still owes; `pagamentos` null when the caller cannot list them. */
export function partesParaReceber(
  divisaoId: string,
  partes: readonly ParteDoServidor[],
  pagamentos: readonly PagamentoDoServidor[] | null,
): ParteParaReceber[] {
  return partes.map((p) => {
    const numero = Number(p.numero);
    const total = Number(p.totalCentavos);
    const jaPago = (pagamentos ?? [])
      .filter((x) => x.estado === "confirmado" && x.divisaoId === divisaoId && Number(x.divisaoParte) === numero)
      .reduce((soma, x) => soma + Number(x.aplicadoCentavos), 0);
    return {
      numero,
      assento: p.assento === null ? null : Number(p.assento),
      totalCentavos: total,
      faltaCentavos: Math.max(0, total - jaPago),
    };
  });
}

/** The balance the receipt aims at: the whole bill, or the part chosen (never more than the bill still owes). */
export function alvoDoRecebimento(saldoCentavos: number, parte: ParteParaReceber | null): number {
  return parte ? Math.min(saldoCentavos, parte.faltaCentavos) : saldoCentavos;
}

/** The sentence for a receipt the server refused. A 403 never says why; a 409 says what changed and the sheet keeps the lines. */
export function textoDoRecebimento(status: number, codigo: string | undefined): string {
  if (status === 403) {
    return "Você não tem permissão para receber desta forma.";
  }

  if (status === 409) {
    switch (codigo) {
      case "versao-desatualizada":
        return "Esta comanda foi alterada em outro aparelho. Atualizamos a conta; confira os valores e receba de novo.";
      case "excede-saldo":
        return "Os valores passam do que falta na conta. Atualizamos a conta; confira os valores.";
      case "excede-parte":
        return "O valor passa do que falta nesta parte. Atualizamos a conta; confira os valores.";
      case "divisao-invalida":
        return "A conta mudou depois de dividida. Divida de novo ou receba a conta toda.";
      case "metodo-inativo":
        return "Um dos métodos foi desativado. Troque o método e receba de novo.";
      case "sem-saldo":
        return "A conta já está paga. Feche a conta.";
      case "comanda-nao-aberta":
        return "Esta comanda não está mais aberta.";
      default:
        break;
    }
  }

  if (status === 400) {
    return "Confira os métodos e os valores e tente de novo.";
  }

  return "Não foi possível registrar o recebimento. Confira os valores e tente de novo.";
}

/** Why the receipt cannot go (in words, to show next to the disabled button); null when it can. */
export function textoDoProblema(p: ProblemaDoRecebimento, metodos: readonly MetodoDeRecebimento[]): string {
  const nome = (id: string) => metodos.find((m) => m.id === id)?.nome ?? "O método";
  switch (p.tipo) {
    case "sem-valor":
      return `Informe o valor de ${nome(p.metodoId)} ou tire o método.`;
    case "excede":
      return "Os métodos passam do que falta. Diminua um valor.";
    case "dinheiro-sem-saldo":
      return "Os outros métodos já cobrem tudo. Tire o dinheiro ou diminua os outros.";
  }
}
