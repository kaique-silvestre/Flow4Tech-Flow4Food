import type { components } from "@flow4tech/api-client";

export type Janela = components["schemas"]["JanelaDto"];
/** The server answers `null` for a closed day in `semana`; the generated schema leaves the null out of the list, so the type says it here. */
export type AgendaDto = Omit<components["schemas"]["AgendaDto"], "semana"> & { semana: (Janela | null)[] };
export type ExcecaoDto = components["schemas"]["ExcecaoDto"];

/** Monday first, the order of the server's `semana`. */
export const DIAS = ["Segunda", "Terça", "Quarta", "Quinta", "Sexta", "Sábado", "Domingo"] as const;

export interface LinhaDaSemana {
  dia: string;
  aberto: boolean;
  abre: string;
  fecha: string;
}

export interface LinhaDeExcecao {
  dia: string;
  fechado: boolean;
  abre: string;
  fecha: string;
}

const HORA = /^([01]\d|2[0-3]):[0-5]\d$/;

/** One line per day from the server's seven windows (null = closed). */
export function montarSemana(semana: readonly (Janela | null)[]): LinhaDaSemana[] {
  return DIAS.map((dia, i) => {
    const janela = semana[i] ?? null;
    return { dia, aberto: janela !== null, abre: janela?.abre ?? "", fecha: janela?.fecha ?? "" };
  });
}

export function paraSemana(linhas: readonly LinhaDaSemana[]): (Janela | null)[] {
  return linhas.map((l) => (l.aberto ? { abre: l.abre, fecha: l.fecha } : null));
}

/** Format only (both times, HH:mm). Whether a window is allowed (the rollover, crossing midnight) is the server's answer, shown as its message. */
function erroDeHoras(abre: string, fecha: string): string | null {
  if (!abre || !fecha) {
    return "Informe a hora de abrir e a de fechar.";
  }

  return HORA.test(abre) && HORA.test(fecha) ? null : "Use o formato HH:mm.";
}

/** Errors by index of the day; days with no problem have no entry. */
export function validarSemana(linhas: readonly LinhaDaSemana[]): Record<number, string> {
  const erros: Record<number, string> = {};
  linhas.forEach((l, i) => {
    const erro = l.aberto ? erroDeHoras(l.abre, l.fecha) : null;
    if (erro) {
      erros[i] = erro;
    }
  });
  return erros;
}

export function montarExcecoes(excecoes: readonly ExcecaoDto[]): LinhaDeExcecao[] {
  return [...excecoes]
    .sort((a, b) => a.dia.localeCompare(b.dia))
    .map((e) => ({ dia: e.dia, fechado: e.janela === null, abre: e.janela?.abre ?? "", fecha: e.janela?.fecha ?? "" }));
}

export function paraExcecoes(linhas: readonly LinhaDeExcecao[]): ExcecaoDto[] {
  return [...linhas]
    .sort((a, b) => a.dia.localeCompare(b.dia))
    .map((l) => ({ dia: l.dia, janela: l.fechado ? null : { abre: l.abre, fecha: l.fecha } }));
}

export function validarExcecao(linha: LinhaDeExcecao): string | null {
  if (!linha.dia) {
    return "Escolha a data.";
  }

  return linha.fechado ? null : erroDeHoras(linha.abre, linha.fecha);
}

export function textoDaJanela(janela: Janela | null): string {
  if (janela === null) {
    return "fechado";
  }

  return janela.abre === janela.fecha ? "o dia todo" : `das ${janela.abre} às ${janela.fecha}`;
}

const SEMANA_DA_DATA = ["domingo", "segunda", "terça", "quarta", "quinta", "sexta", "sábado"] as const;

/** `2026-12-25` as `25/12/2026 · sexta` (the date is an operating day, no time zone is applied). */
export function dataBr(dia: string): string {
  const [ano, mes, dd] = dia.split("-").map(Number) as [number, number, number];
  const nome = SEMANA_DA_DATA[new Date(Date.UTC(ano, mes - 1, dd)).getUTCDay()];
  return `${String(dd).padStart(2, "0")}/${String(mes).padStart(2, "0")}/${ano} · ${nome}`;
}

const GENERICA = "Não foi possível salvar. Tente de novo.";

/** What the owner reads for each refusal of the agenda routes (400, 403, 409). The code never reaches the screen; the rollover hour is the server's (`virada`). */
export function mensagemDeErro(status: number, codigo: string | undefined, virada: string): string {
  if (status === 403) {
    return "Você não tem permissão para isso.";
  }

  switch (codigo) {
    case "excecoes-demais":
      return "Cada unidade guarda até 10 exceções. Remova alguma antes de incluir outra.";
    case "invalido":
      return `Horário não aceito. Abra às ${virada} ou depois; se fechar depois da meia-noite, feche até ${virada}.`;
    case "travada":
      return "A organização travou o horário de funcionamento: a unidade não pode alterá-lo.";
    case "fora-do-horario":
      return "Lançamento recusado: a unidade está fora do horário de funcionamento.";
    case "sem-horario-para-estender":
      return "Hoje a unidade está fechada ou aberta o dia todo: não há horário de fechamento para estender.";
    case "extensao-invalida":
      return `O novo fechamento precisa ser depois do fechamento atual e depois de agora, e até ${virada} do dia seguinte.`;
    case "sem-unidade-ativa":
      return "Escolha a unidade em que você está trabalhando e tente de novo.";
    default:
      return GENERICA;
  }
}
