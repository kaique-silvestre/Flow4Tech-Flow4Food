import { describe, expect, it } from "vitest";
import {
  acoesPermitidas,
  ehOProprio,
  nomesDasUnidades,
  nomesDosPerfis,
  type Perfil,
  type Pessoa,
  textoDeAcoes,
} from "./logica";

const pessoa = (extra: Partial<Pessoa> = {}): Pessoa => ({
  id: "p1",
  nome: "Ana",
  usuario: "ana",
  dono: false,
  ativa: true,
  unidadeIds: ["u1", "u9"],
  perfilIds: ["g"],
  ...extra,
});
const perfil = (id: string, nome: string, efeitos: string[] = []): Perfil => ({
  id,
  nome,
  modelo: null,
  acompanhaModelo: false,
  regras: efeitos.map((efeito) => ({ efeito, permissao: "x:y" }) as Perfil["regras"][number]),
});

describe("pessoas logic", () => {
  it("knows the signed-in person, and nobody when there is no session", () => {
    expect(ehOProprio(pessoa(), "p1")).toBe(true);
    expect(ehOProprio(pessoa(), "p2")).toBe(false);
    expect(ehOProprio(pessoa(), null)).toBe(false);
  });

  it("names the profiles the person holds and ignores ids it does not know", () => {
    const perfis = [perfil("c", "Caixa"), perfil("g", "Garçom")];
    expect(nomesDosPerfis(pessoa({ perfilIds: ["g", "sumiu"] }), perfis)).toEqual(["Garçom"]);
  });

  it("names units and never prints the id of one the caller does not see", () => {
    expect(nomesDasUnidades(pessoa(), new Map([["u1", "Centro"]]))).toEqual(["Centro"]);
  });

  it("counts only the allow rules and pluralizes", () => {
    expect(acoesPermitidas(perfil("a", "A", ["allow", "deny", "allow"]))).toBe(2);
    expect(textoDeAcoes(1)).toBe("1 ação permitida");
    expect(textoDeAcoes(0)).toBe("0 ações permitidas");
  });
});
