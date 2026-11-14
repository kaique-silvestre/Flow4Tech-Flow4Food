import type { components } from "@flow4tech/api-client";
import { centsFromDigits, formatBRL } from "@flow4tech/design-system";

type Esquemas = components["schemas"];

type Numero = number | string | null | undefined;

function n(v: Numero): number | null {
  return v === null || v === undefined ? null : Number(v);
}

export type SituacaoDoTurno = "concluida" | "pendente" | "aprovada";

/** A shift as the screen reads it: every int64 turned into a number, absent values null. */
export interface Turno {
  id: string;
  operadorId: string;
  abertoEm: string;
  fechadoEm: string | null;
  fundoCentavos: number;
  recebidoDinheiroCentavos: number;
  contadoCentavos: number | null;
  esperadoCentavos: number | null;
  esperadoOculto: boolean;
  diferencaCentavos: number | null;
  diferencaTexto: string | null;
  situacao: SituacaoDoTurno;
  suprimentosCentavos: number;
  sangriasCentavos: number;
  contagens: Contagem[];
}

export interface Contagem {
  metodoId: string;
  metodoNome: string;
  contadoCentavos: number;
  esperadoCentavos: number;
  diferencaCentavos: number;
}

export interface Movimento {
  id: string;
  tipo: "suprimento" | "sangria";
  valorCentavos: number;
  motivo: string;
  destino: string | null;
  criadoEm: string;
}

interface Aprovacao {
  id: string;
  tipo: "aprovacao" | "recontagem";
  motivo: string;
  diferencaAntesCentavos: number;
  diferencaDepoisCentavos: number;
  criadoEm: string;
}

interface EsperadoDoMetodo {
  metodoId: string;
  metodoNome: string;
  /** Null while the count is blind. */
  esperadoCentavos: number | null;
}

export interface Resumo {
  turno: Turno;
  cego: boolean;
  suprimentosCentavos: number;
  sangriasCentavos: number;
  esperadoCentavos: number | null;
  porMetodo: EsperadoDoMetodo[];
}

export interface DetalheDoTurno {
  turno: Turno;
  contagens: Contagem[];
  movimentos: Movimento[];
  aprovacoes: Aprovacao[];
}

function situacaoDe(v: string | null | undefined): SituacaoDoTurno {
  return v === "pendente" || v === "aprovada" ? v : "concluida";
}

function paraContagem(c: Esquemas["ContagemDto"]): Contagem {
  return {
    metodoId: c.metodoId,
    metodoNome: c.metodoNome,
    contadoCentavos: Number(c.contadoCentavos),
    esperadoCentavos: Number(c.esperadoCentavos),
    diferencaCentavos: Number(c.diferencaCentavos),
  };
}

export function paraTurno(t: Esquemas["TurnoDto"]): Turno {
  return {
    id: t.id,
    operadorId: t.operadorId,
    abertoEm: t.abertoEm,
    fechadoEm: t.fechadoEm,
    fundoCentavos: Number(t.fundoCentavos),
    recebidoDinheiroCentavos: Number(t.recebidoDinheiroCentavos),
    contadoCentavos: n(t.contadoCentavos),
    esperadoCentavos: n(t.esperadoCentavos),
    esperadoOculto: t.esperadoOculto,
    diferencaCentavos: n(t.diferencaCentavos),
    diferencaTexto: t.diferencaTexto ?? null,
    situacao: situacaoDe(t.situacao),
    suprimentosCentavos: n(t.suprimentosCentavos) ?? 0,
    sangriasCentavos: n(t.sangriasCentavos) ?? 0,
    contagens: (t.contagens ?? []).map(paraContagem),
  };
}

export function paraMovimento(m: Esquemas["MovimentoDto"]): Movimento {
  return {
    id: m.id,
    tipo: m.tipo === "sangria" ? "sangria" : "suprimento",
    valorCentavos: Number(m.valorCentavos),
    motivo: m.motivo,
    destino: m.destino,
    criadoEm: m.criadoEm,
  };
}

export function paraResumo(r: Esquemas["ResumoDoTurnoDto"]): Resumo {
  return {
    turno: paraTurno(r.turno),
    cego: r.cego,
    suprimentosCentavos: Number(r.suprimentosCentavos),
    sangriasCentavos: Number(r.sangriasCentavos),
    esperadoCentavos: n(r.esperadoCentavos),
    porMetodo: r.porMetodo.map((m) => ({
      metodoId: m.metodoId,
      metodoNome: m.metodoNome,
      esperadoCentavos: n(m.esperadoCentavos),
    })),
  };
}

export function paraDetalhe(d: Esquemas["TurnoDetalheDto"]): DetalheDoTurno {
  return {
    turno: paraTurno(d.turno),
    contagens: d.contagens.map(paraContagem),
    movimentos: d.movimentos.map(paraMovimento),
    aprovacoes: d.aprovacoes.map((a) => ({
      id: a.id,
      tipo: a.tipo === "recontagem" ? "recontagem" : "aprovacao",
      motivo: a.motivo,
      diferencaAntesCentavos: Number(a.diferencaAntesCentavos),
      diferencaDepoisCentavos: Number(a.diferencaDepoisCentavos),
      criadoEm: a.criadoEm,
    })),
  };
}

export function hora(iso: string): string {
  return new Date(iso).toLocaleTimeString("pt-BR", { hour: "2-digit", minute: "2-digit" });
}

export function diaEHora(iso: string): string {
  return new Date(iso).toLocaleString("pt-BR", {
    day: "2-digit",
    month: "2-digit",
    hour: "2-digit",
    minute: "2-digit",
  });
}

/** Tone of the difference in words: exact is calm, a shortage is an alert, a surplus is a warning. Never color alone: the word is always there. */
export function tomDaDiferenca(diferencaCentavos: number): "success" | "alert" | "warning" {
  return diferencaCentavos === 0 ? "success" : diferencaCentavos < 0 ? "alert" : "warning";
}

/** The difference in words, the way the server says it for cash ("Confere", "Falta R$ 5,00", "Sobra R$ 12,00"). */
export function palavraDaDiferenca(diferencaCentavos: number): string {
  if (diferencaCentavos === 0) {
    return "Confere";
  }

  return `${diferencaCentavos < 0 ? "Falta" : "Sobra"} ${formatBRL(Math.abs(diferencaCentavos))}`;
}

/** The total difference of a count: |cash| + the sum of |each method|, the same sum the server measures against the tolerance. */
export function diferencaTotal(dinheiroCentavos: number, porMetodoCentavos: readonly number[]): number {
  return Math.abs(dinheiroCentavos) + porMetodoCentavos.reduce((soma, d) => soma + Math.abs(d), 0);
}

/** What the screen is NOT allowed to know while the count is blind: the expected value of the open shift. */
export function esperadoDaGaveta(resumo: Resumo): number | null {
  if (resumo.cego || resumo.turno.esperadoOculto || resumo.esperadoCentavos === null) {
    return null;
  }

  return resumo.esperadoCentavos;
}

/** The method lines the person filled in; a blank one was not counted and is not sent. */
export function montarContagens(
  digitosPorMetodo: Readonly<Record<string, string>>,
): { metodoId: string; contadoCentavos: number }[] {
  return Object.entries(digitosPorMetodo)
    .filter(([, digitos]) => digitos !== "")
    .map(([metodoId, digitos]) => ({ metodoId, contadoCentavos: centsFromDigits(digitos) }));
}

export type TipoDeMovimento = "suprimento" | "sangria";

export const LIMITE_DO_MOTIVO = 200;
export const LIMITE_DO_DESTINO = 80;

export interface RascunhoDeMovimento {
  tipo: TipoDeMovimento;
  valorDigitos: string;
  motivo: string;
  destino: string;
}

/** Why the sheet cannot be sent yet; null when it can. The server checks the same limits and decides the rest. */
export function problemaDoMovimento(r: RascunhoDeMovimento): "valor" | "motivo" | "destino" | null {
  if (r.valorDigitos === "" || centsFromDigits(r.valorDigitos) <= 0) {
    return "valor";
  }

  const motivo = r.motivo.trim();
  if (motivo === "" || motivo.length > LIMITE_DO_MOTIVO) {
    return "motivo";
  }

  const destino = r.destino.trim();
  if ((r.tipo === "sangria" && destino === "") || destino.length > LIMITE_DO_DESTINO) {
    return "destino";
  }

  return null;
}

export const SEM_SINAL =
  "Sem sinal. Suprimento, sangria, fechamento e aprovação precisam do servidor e não ficam na fila.";

/** The sentence for a refused movement. A 403 never says why. */
export function textoDoMovimento(tipo: TipoDeMovimento, status: number, codigo?: string): string {
  const nome = tipo === "sangria" ? "a sangria" : "o suprimento";
  if (status === 403) {
    return `Você não tem permissão para registrar ${nome} com este valor.`;
  }

  if (status === 404) {
    return "Este caixa já foi fechado. Atualize a tela.";
  }

  if (status === 409 && codigo === "saldo-insuficiente") {
    return "A gaveta não tem esse valor para tirar. Confira o valor e tente de novo.";
  }

  if (status === 400) {
    return "Confira o valor, o motivo e o destino e tente de novo.";
  }

  return `Não foi possível registrar ${nome}. Tente de novo.`;
}

export function textoDoFechamento(status: number): string {
  if (status === 404) {
    return "Este caixa já foi fechado. Atualize a tela.";
  }

  if (status === 403) {
    return "Você não tem permissão para fechar o caixa.";
  }

  if (status === 400) {
    return "Confira os valores contados e tente de novo.";
  }

  return "Não foi possível fechar o caixa. Tente de novo.";
}

export function textoDaDecisao(acao: "aprovar" | "recontar", status: number, codigo?: string): string {
  if (status === 403) {
    return "Você não tem permissão para decidir esta diferença.";
  }

  if (status === 409 && codigo === "autoaprovacao-nao-permitida") {
    return "Quem fechou o caixa não decide a própria diferença. Peça a outra pessoa.";
  }

  if (status === 409 && codigo === "nao-pendente") {
    return "Esta diferença já foi decidida por outra pessoa. A lista foi atualizada.";
  }

  if (status === 404) {
    return "Este caixa não foi encontrado. A lista foi atualizada.";
  }

  if (status === 400) {
    return "Confira o motivo e os valores e tente de novo.";
  }

  return acao === "aprovar"
    ? "Não foi possível aprovar. Tente de novo."
    : "Não foi possível registrar a recontagem. Tente de novo.";
}

export const PALAVRA_DA_SITUACAO: Record<SituacaoDoTurno, string> = {
  concluida: "Conferido",
  pendente: "Aguardando aprovação",
  aprovada: "Diferença aprovada",
};
