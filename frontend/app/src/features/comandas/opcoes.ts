import type { GrupoDeOpcao, Opcao, OpcaoFrequente } from "./cardapio";
import type { OpcaoEscolhida } from "./rodada";

/** How many add-ons the "mais usados" row offers: what fits on one phone line pair without scrolling the sheet. */
const MAIS_USADOS_MAXIMOS = 4;

/** A group never demands more than it still offers (an add-on that ran out must not block the service), the same rule the server applies. */
export function minimoEfetivo(grupo: GrupoDeOpcao): number {
  return Math.min(grupo.minimo, grupo.opcoes.length);
}

/** The sheet opens by itself only when something must be chosen; otherwise a product adds in one tap. */
export function exigeEscolha(grupos: readonly GrupoDeOpcao[]): boolean {
  return grupos.some((g) => minimoEfetivo(g) >= 1);
}

function paraEscolhida(grupo: GrupoDeOpcao, opcao: Opcao): OpcaoEscolhida {
  return { id: opcao.id, grupoId: grupo.id, nome: opcao.nome, precoCentavos: opcao.precoCentavos };
}

/** Toggles an add-on: a second tap removes it; in a group of one the new pick replaces the old; a full group refuses more. */
export function alternar(escolhidas: readonly OpcaoEscolhida[], grupo: GrupoDeOpcao, opcao: Opcao): OpcaoEscolhida[] {
  if (escolhidas.some((e) => e.id === opcao.id)) {
    return escolhidas.filter((e) => e.id !== opcao.id);
  }

  const doGrupo = escolhidas.filter((e) => e.grupoId === grupo.id);
  if (grupo.maximo === 1) {
    return [...escolhidas.filter((e) => e.grupoId !== grupo.id), paraEscolhida(grupo, opcao)];
  }

  if (doGrupo.length >= grupo.maximo) {
    return [...escolhidas];
  }

  return [...escolhidas, paraEscolhida(grupo, opcao)];
}

export function grupoCheio(grupo: GrupoDeOpcao, escolhidas: readonly OpcaoEscolhida[]): boolean {
  return grupo.maximo > 1 && escolhidas.filter((e) => e.grupoId === grupo.id).length >= grupo.maximo;
}

/** Groups still short of their minimum, with a plain sentence each. */
export function pendencias(grupos: readonly GrupoDeOpcao[], escolhidas: readonly OpcaoEscolhida[]): string[] {
  return grupos.flatMap((g) => {
    const minimo = minimoEfetivo(g);
    const feitas = escolhidas.filter((e) => e.grupoId === g.id).length;
    if (feitas >= minimo) {
      return [];
    }

    return [minimo === 1 ? `Escolha uma opção em ${g.nome}.` : `Escolha pelo menos ${minimo} opções em ${g.nome}.`];
  });
}

/** The most used add-ons of this product that can still be picked, most used first (ties keep the server order). */
export function maisUsados(
  grupos: readonly GrupoDeOpcao[],
  frequentes: readonly OpcaoFrequente[],
  limite = MAIS_USADOS_MAXIMOS,
): { grupo: GrupoDeOpcao; opcao: Opcao }[] {
  const achados = frequentes.flatMap((f) => {
    const grupo = grupos.find((g) => g.id === f.grupoId);
    const opcao = grupo?.opcoes.find((o) => o.id === f.opcao.id);
    return grupo && opcao ? [{ grupo, opcao, lancamentos: f.lancamentos }] : [];
  });
  return achados
    .map((a, ordem) => ({ ...a, ordem }))
    .sort((a, b) => b.lancamentos - a.lancamentos || a.ordem - b.ordem)
    .slice(0, limite)
    .map(({ grupo, opcao }) => ({ grupo, opcao }));
}

/** "Bacon, Queijo" for a row's second line. */
export function resumoDasOpcoes(opcoes: readonly { nome: string }[]): string {
  return opcoes.map((o) => o.nome).join(", ");
}
