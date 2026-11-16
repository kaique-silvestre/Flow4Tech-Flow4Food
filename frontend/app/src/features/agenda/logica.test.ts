import { describe, expect, it } from "vitest";
import {
  type AgendaDto,
  DIAS,
  dataBr,
  mensagemDeErro,
  montarExcecoes,
  montarSemana,
  paraExcecoes,
  paraSemana,
  textoDaJanela,
  validarExcecao,
  validarSemana,
} from "./logica";

const janela = (abre: string, fecha: string) => ({ abre, fecha });
const semana: AgendaDto["semana"] = [
  null,
  janela("18:00", "23:00"),
  janela("18:00", "23:00"),
  janela("18:00", "23:00"),
  janela("18:00", "23:00"),
  janela("17:00", "03:00"),
  janela("15:00", "03:00"),
];

describe("semana a partir da resposta", () => {
  it("starts on Monday, one line per day, null is closed", () => {
    const linhas = montarSemana(semana);
    expect(DIAS[0]).toBe("Segunda");
    expect(linhas).toHaveLength(7);
    expect(linhas[0]).toEqual({ dia: "Segunda", aberto: false, abre: "", fecha: "" });
    expect(linhas[4]).toEqual({ dia: "Sexta", aberto: true, abre: "18:00", fecha: "23:00" });
    expect(linhas[6]?.fecha).toBe("03:00");
  });

  it("goes back to the request: closed day is null, open day carries both times", () => {
    expect(paraSemana(montarSemana(semana))).toEqual(semana);
  });
});

describe("validacao de formato da semana", () => {
  it("asks for both times of an open day and accepts equal times (whole day, the server decides the rest)", () => {
    const linhas = montarSemana(semana);
    linhas[1] = { dia: "Terça", aberto: true, abre: "", fecha: "23:00" };
    linhas[2] = { dia: "Quarta", aberto: true, abre: "10:00", fecha: "10:00" };
    linhas[3] = { dia: "Quinta", aberto: true, abre: "25:00", fecha: "23:00" };
    const erros = validarSemana(linhas);
    expect(erros[1]).toBe("Informe a hora de abrir e a de fechar.");
    expect(erros[2]).toBeUndefined();
    expect(erros[3]).toBe("Use o formato HH:mm.");
    expect(erros[0]).toBeUndefined();
  });

  it("does not judge a window that crosses midnight: that is the server's call", () => {
    expect(validarSemana(montarSemana(semana))).toEqual({});
  });
});

describe("excecoes", () => {
  it("lists the response in date order with the closed and the special hours", () => {
    const linhas = montarExcecoes([
      { dia: "2026-12-25", janela: null },
      { dia: "2026-11-20", janela: janela("14:00", "03:00") },
    ]);
    expect(linhas.map((l) => l.dia)).toEqual(["2026-11-20", "2026-12-25"]);
    expect(linhas[1]).toEqual({ dia: "2026-12-25", fechado: true, abre: "", fecha: "" });
    expect(paraExcecoes(linhas)).toEqual([
      { dia: "2026-11-20", janela: janela("14:00", "03:00") },
      { dia: "2026-12-25", janela: null },
    ]);
  });

  it("checks only format before sending", () => {
    expect(validarExcecao({ dia: "", fechado: true, abre: "", fecha: "" })).toBe("Escolha a data.");
    expect(validarExcecao({ dia: "2026-12-25", fechado: false, abre: "", fecha: "03:00" })).toBe(
      "Informe a hora de abrir e a de fechar.",
    );
    expect(validarExcecao({ dia: "2026-12-25", fechado: true, abre: "", fecha: "" })).toBeNull();
    expect(validarExcecao({ dia: "2026-12-25", fechado: false, abre: "14:00", fecha: "03:00" })).toBeNull();
  });
});

describe("textos", () => {
  it("writes the window and the date", () => {
    expect(textoDaJanela(janela("17:00", "03:00"))).toBe("das 17:00 às 03:00");
    expect(textoDaJanela(janela("10:00", "10:00"))).toBe("o dia todo");
    expect(textoDaJanela(null)).toBe("fechado");
    expect(dataBr("2026-12-25")).toBe("25/12/2026 · sexta");
  });
});

describe("mensagens do contrato", () => {
  it("names each answer of the contract without leaking a code", () => {
    expect(mensagemDeErro(400, "excecoes-demais", "05:00")).toMatch(/10 exceções/);
    expect(mensagemDeErro(400, "invalido", "05:00")).toMatch(/05:00/);
    expect(mensagemDeErro(409, "travada", "05:00")).toMatch(/organização travou/);
    expect(mensagemDeErro(409, "fora-do-horario", "05:00")).toMatch(/fora do horário/);
    expect(mensagemDeErro(409, "sem-horario-para-estender", "05:00")).toMatch(/fechada ou aberta o dia todo/);
    expect(mensagemDeErro(400, "extensao-invalida", "05:00")).toMatch(/depois do fechamento/);
    expect(mensagemDeErro(403, undefined, "05:00")).toBe("Você não tem permissão para isso.");
    expect(mensagemDeErro(500, undefined, "05:00")).toBe("Não foi possível salvar. Tente de novo.");
    expect(mensagemDeErro(400, "outra-coisa", "05:00")).toBe("Não foi possível salvar. Tente de novo.");
  });
});
