import type { components } from "@flow4tech/api-client";

export type Pessoa = components["schemas"]["ContaDaListaDto"];
export type Perfil = components["schemas"]["PerfilDto"];

/** True when the person on screen is the one signed in: the server refuses a change of one's own profile (403, no reason), so the screen says it BEFORE sending. */
export function ehOProprio(pessoa: Pessoa, contaDaSessao: string | null): boolean {
  return contaDaSessao !== null && pessoa.id === contaDaSessao;
}

/** The profiles of a person by name, in the order of the catalog of profiles; ids the list does not know (a profile removed meanwhile) are left out. */
export function nomesDosPerfis(pessoa: Pessoa, perfis: readonly Perfil[]): string[] {
  return perfis.filter((p) => pessoa.perfilIds.includes(p.id)).map((p) => p.nome);
}

/** The unit names of a person; a unit the caller does not see keeps no name (never an id on screen). */
export function nomesDasUnidades(pessoa: Pessoa, unidades: ReadonlyMap<string, string>): string[] {
  return pessoa.unidadeIds.flatMap((id) => {
    const nome = unidades.get(id);
    return nome ? [nome] : [];
  });
}

/** How many actions a profile allows (rules with effect allow): the sheet shows it so the owner sees the weight of the choice before tapping. */
export function acoesPermitidas(perfil: Perfil): number {
  return perfil.regras.filter((r) => r.efeito === "allow").length;
}

export function textoDeAcoes(n: number): string {
  return n === 1 ? "1 ação permitida" : `${n} ações permitidas`;
}
