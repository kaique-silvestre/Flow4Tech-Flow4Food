import type { components } from "@flow4tech/api-client";
import { formatBRL } from "@flow4tech/design-system";
import { screen, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, describe, expect, it, vi } from "vitest";
import { json, renderEm, sessaoJson, stubRede } from "../../test-utils";

/** The currency has a no-break space; the text matchers of Testing Library normalise it to a plain one. */
const brl = (centavos: number) => formatBRL(centavos).replace(/\u00a0/g, " ");
const texto = (el: Element) => (el.textContent ?? "").replace(/\u00a0/g, " ");

afterEach(() => {
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

type Turno = components["schemas"]["TurnoDto"];
const pronta = { "POST /refresh": json(sessaoJson()), "GET /comandas": json([]) };
const PIX = "0197e2e0-0000-7000-8000-0000000000b1";
const PERMISSOES = ["caixa:ver", "caixa:abrir", "caixa:fechar", "comanda:ver"];
const comPermissoes = (...extra: string[]) => ({
  "GET /sessao/permissoes": json({ permissoes: [...PERMISSOES, ...extra] }),
});

const turno = (extra: Partial<Turno> = {}): Turno => ({
  id: "t1",
  unidadeId: "u1",
  operadorId: "o1",
  abertoEm: "2026-11-06T14:30:00Z",
  fundoCentavos: 10_000,
  fechadoEm: null,
  contadoCentavos: null,
  esperadoCentavos: null,
  esperadoOculto: false,
  diferencaCentavos: null,
  diferencaTexto: null,
  recebidoDinheiroCentavos: 2_750,
  ...extra,
});
const resumo = (extra: Partial<components["schemas"]["ResumoDoTurnoDto"]> = {}) =>
  json({
    turno: turno(),
    cego: false,
    suprimentosCentavos: 5_000,
    sangriasCentavos: 2_000,
    esperadoCentavos: 15_750,
    porMetodo: [{ metodoId: PIX, metodoNome: "Pix", esperadoCentavos: 4_000 }],
    ...extra,
  });
const movimento = (
  extra: Partial<components["schemas"]["MovimentoDto"]> = {},
): components["schemas"]["MovimentoDto"] => ({
  id: "m1",
  turnoId: "t1",
  tipo: "suprimento",
  valorCentavos: 5_000,
  motivo: "Mais troco",
  destino: null,
  operadorId: "o1",
  criadoEm: "2026-11-06T15:00:00Z",
  ...extra,
});
const aberto = (movimentos: unknown = [movimento()], r: Response = resumo()) => ({
  ...pronta,
  ...comPermissoes(),
  "GET /caixa/turnos/atual/resumo": r,
  "GET /caixa/turnos/atual/movimentos": json(movimentos),
});

const aoServidor = (chamadas: { method: string; path: string; body: unknown }[], fim: string) =>
  chamadas.find((c) => c.method === "POST" && c.path.endsWith(fim));

describe("movements of the shift", () => {
  it("lists supplies and withdrawals with value, reason and destination, and the totals", async () => {
    stubRede(
      aberto([
        movimento(),
        movimento({ id: "m2", tipo: "sangria", valorCentavos: 2_000, motivo: "Ao cofre", destino: "Cofre" }),
      ]),
    );

    await renderEm("/salao/caixa");

    const lista = await screen.findByRole("heading", { level: 3, name: "Movimentos do turno" });
    const secao = lista.closest("section")!;
    expect(await within(secao).findByText("Mais troco")).toBeVisible();
    expect(within(secao).getByText("Ao cofre · Cofre")).toBeVisible();
    expect(texto(secao)).toContain(`+${brl(5_000)}`);
    expect(texto(secao)).toContain(`−${brl(2_000)}`);
    const valores = screen.getByText("Suprimentos").closest("dl")!;
    expect(texto(valores)).toContain(brl(5_000));
    expect(texto(valores)).toContain(brl(2_000));
    expect(texto(within(valores).getByText("Esperado na gaveta").parentElement!)).toContain(brl(15_750));
  });

  it("says so when there are none and offers a retry when the list fails", async () => {
    let falhar = true;
    stubRede({
      ...aberto([]),
      "GET /caixa/turnos/atual/movimentos": () => (falhar ? new Response(null, { status: 500 }) : json([])),
    });
    await renderEm("/salao/caixa");

    expect(await screen.findByText("Não foi possível carregar os movimentos.")).toBeVisible();
    falhar = false;
    await userEvent.click(screen.getByRole("button", { name: "Tentar novamente" }));

    expect(await screen.findByText("Nenhum suprimento ou sangria neste turno.")).toBeVisible();
  });
});

describe("supply and withdrawal sheets", () => {
  it("sends a supply with value, reason and optional origin, then refreshes and says it was done", async () => {
    let feito = false;
    const chamadas = stubRede({
      ...aberto(),
      "GET /caixa/turnos/atual/movimentos": () =>
        json(feito ? [movimento(), movimento({ id: "m3", motivo: "Reforço" })] : [movimento()]),
      "POST /caixa/turnos/atual/suprimentos": () => {
        feito = true;
        return json(movimento({ id: "m3", motivo: "Reforço" }), { status: 201 });
      },
    });
    await renderEm("/salao/caixa");
    await userEvent.click(await screen.findByRole("button", { name: "Suprimento" }));
    const folha = await screen.findByRole("dialog", { name: "Suprimento" });
    expect(within(folha).getByRole("button", { name: "Registrar suprimento" })).toBeDisabled();

    await userEvent.type(within(folha).getByLabelText("Valor"), "5000");
    await userEvent.type(within(folha).getByLabelText("Motivo"), "Reforço");
    await userEvent.click(within(folha).getByRole("button", { name: "Registrar suprimento" }));

    expect(await screen.findByText((t: string) => t.includes(`Suprimento de ${brl(5_000)} registrado`))).toBeVisible();
    expect(aoServidor(chamadas, "/suprimentos")?.body).toEqual({
      valorCentavos: 5_000,
      motivo: "Reforço",
      destino: null,
    });
    expect(await screen.findByText("Reforço")).toBeVisible();
  });

  it("needs a destination for a withdrawal", async () => {
    const chamadas = stubRede({
      ...aberto(),
      "POST /caixa/turnos/atual/sangrias": json(movimento({ tipo: "sangria" }), { status: 201 }),
    });
    await renderEm("/salao/caixa");
    await userEvent.click(await screen.findByRole("button", { name: "Sangria" }));
    const folha = await screen.findByRole("dialog", { name: "Sangria" });
    await userEvent.type(within(folha).getByLabelText("Valor"), "3000");
    await userEvent.type(within(folha).getByLabelText("Motivo"), "Ao cofre");
    const enviar = within(folha).getByRole("button", { name: "Registrar sangria" });
    expect(enviar).toBeDisabled();

    await userEvent.type(within(folha).getByLabelText("Destino do dinheiro"), "Cofre");
    expect(enviar).toBeEnabled();
    await userEvent.click(enviar);

    await vi.waitFor(() =>
      expect(aoServidor(chamadas, "/sangrias")?.body).toEqual({
        valorCentavos: 3_000,
        motivo: "Ao cofre",
        destino: "Cofre",
      }),
    );
  });

  it("keeps what was typed and tells the 403 without saying why", async () => {
    stubRede({ ...aberto(), "POST /caixa/turnos/atual/sangrias": new Response(null, { status: 403 }) });
    await renderEm("/salao/caixa");
    await userEvent.click(await screen.findByRole("button", { name: "Sangria" }));
    const folha = await screen.findByRole("dialog", { name: "Sangria" });
    await userEvent.type(within(folha).getByLabelText("Valor"), "900000");
    await userEvent.type(within(folha).getByLabelText("Motivo"), "Ao cofre");
    await userEvent.type(within(folha).getByLabelText("Destino do dinheiro"), "Cofre");

    await userEvent.click(within(folha).getByRole("button", { name: "Registrar sangria" }));

    expect(await within(folha).findByRole("alert")).toHaveTextContent(
      "Você não tem permissão para registrar a sangria com este valor.",
    );
    expect(within(folha).getByLabelText("Motivo")).toHaveValue("Ao cofre");
  });

  it("explains a withdrawal bigger than the drawer", async () => {
    stubRede({
      ...aberto(),
      "POST /caixa/turnos/atual/sangrias": json({ codigo: "saldo-insuficiente" }, { status: 409 }),
    });
    await renderEm("/salao/caixa");
    await userEvent.click(await screen.findByRole("button", { name: "Sangria" }));
    const folha = await screen.findByRole("dialog", { name: "Sangria" });
    await userEvent.type(within(folha).getByLabelText("Valor"), "900000");
    await userEvent.type(within(folha).getByLabelText("Motivo"), "x");
    await userEvent.type(within(folha).getByLabelText("Destino do dinheiro"), "y");

    await userEvent.click(within(folha).getByRole("button", { name: "Registrar sangria" }));

    expect(await within(folha).findByRole("alert")).toHaveTextContent(/gaveta não tem esse valor/);
  });

  it("is blocked with no signal and never goes to the queue", async () => {
    vi.spyOn(navigator, "onLine", "get").mockReturnValue(false);
    const chamadas = stubRede(aberto());
    await renderEm("/salao/caixa");
    await userEvent.click(await screen.findByRole("button", { name: "Suprimento" }));
    const folha = await screen.findByRole("dialog", { name: "Suprimento" });
    await userEvent.type(within(folha).getByLabelText("Valor"), "5000");
    await userEvent.type(within(folha).getByLabelText("Motivo"), "Reforço");

    expect(within(folha).getByText(/Sem sinal/)).toBeVisible();
    expect(within(folha).getByRole("button", { name: "Registrar suprimento" })).toBeDisabled();
    expect(aoServidor(chamadas, "/suprimentos")).toBeUndefined();
  });

  it("says there is no connection when the request fails", async () => {
    stubRede({
      ...aberto(),
      "POST /caixa/turnos/atual/suprimentos": () => {
        throw new TypeError("Failed to fetch");
      },
    });
    await renderEm("/salao/caixa");
    await userEvent.click(await screen.findByRole("button", { name: "Suprimento" }));
    const folha = await screen.findByRole("dialog", { name: "Suprimento" });
    await userEvent.type(within(folha).getByLabelText("Valor"), "5000");
    await userEvent.type(within(folha).getByLabelText("Motivo"), "Reforço");

    await userEvent.click(within(folha).getByRole("button", { name: "Registrar suprimento" }));

    expect(await within(folha).findByRole("alert")).toHaveTextContent(/Sem conexão/);
  });
});

describe("close with the count by method", () => {
  const fechado = (extra: Partial<Turno> = {}): Turno =>
    turno({
      fechadoEm: "2026-11-06T22:00:00Z",
      contadoCentavos: 15_750,
      esperadoCentavos: 15_750,
      diferencaCentavos: 0,
      diferencaTexto: "Confere",
      situacao: "concluida",
      contagens: [
        {
          metodoId: PIX,
          metodoNome: "Pix",
          contadoCentavos: 3_000,
          esperadoCentavos: 4_000,
          diferencaCentavos: -1_000,
        },
      ],
      ...extra,
    });

  it("shows the expected value of each method, the live difference in words, and sends only the lines filled in", async () => {
    const chamadas = stubRede({ ...aberto(), "POST /caixa/turnos/atual/fechar": json(fechado()) });
    await renderEm("/salao/caixa");
    await userEvent.click(await screen.findByRole("button", { name: "Fechar caixa" }));
    const folha = await screen.findByRole("dialog", { name: "Fechar caixa" });
    expect(within(folha).getByText(`Esperado na gaveta: ${brl(15_750)}`)).toBeVisible();
    expect(within(folha).getByText((t) => t.includes(`Esperado: ${brl(4_000)}`))).toBeVisible();

    await userEvent.type(within(folha).getByLabelText("Dinheiro contado"), "15250");
    expect(within(folha).getByText(`Falta ${brl(500)}`)).toBeVisible();
    await userEvent.type(within(folha).getByLabelText("Pix conferido"), "3000");
    expect(within(folha).getByText(`Falta ${brl(1_000)}`)).toBeVisible();
    await userEvent.click(within(folha).getByRole("button", { name: "Fechar caixa" }));

    const resultado = await screen.findByRole("dialog", { name: "Caixa fechado" });
    expect(within(resultado).getByRole("list", { name: "Conferência por método" })).toBeVisible();
    expect(within(resultado).getByText("Pix")).toBeVisible();
    expect(within(resultado).getByText(`Falta ${brl(1_000)}`)).toBeVisible();
    expect(aoServidor(chamadas, "/fechar")?.body).toEqual({
      contadoCentavos: 15_250,
      contagens: [{ metodoId: PIX, contadoCentavos: 3_000 }],
    });
  });

  it("does not send a method left blank", async () => {
    const chamadas = stubRede({ ...aberto(), "POST /caixa/turnos/atual/fechar": json(fechado({ contagens: [] })) });
    await renderEm("/salao/caixa");
    await userEvent.click(await screen.findByRole("button", { name: "Fechar caixa" }));
    const folha = await screen.findByRole("dialog", { name: "Fechar caixa" });
    await userEvent.type(within(folha).getByLabelText("Dinheiro contado"), "15750");

    await userEvent.click(within(folha).getByRole("button", { name: "Fechar caixa" }));

    await screen.findByRole("dialog", { name: "Caixa fechado" });
    expect(aoServidor(chamadas, "/fechar")?.body).toEqual({ contadoCentavos: 15_750 });
  });

  it("tells that a difference above the tolerance waits for a manager", async () => {
    stubRede({
      ...aberto(),
      "POST /caixa/turnos/atual/fechar": json(
        fechado({
          contadoCentavos: 14_000,
          diferencaCentavos: -1_750,
          diferencaTexto: `Falta ${brl(1_750)}`,
          situacao: "pendente",
        }),
      ),
    });
    await renderEm("/salao/caixa");
    await userEvent.click(await screen.findByRole("button", { name: "Fechar caixa" }));
    await userEvent.type(within(await screen.findByRole("dialog")).getByLabelText("Dinheiro contado"), "14000");
    await userEvent.click(within(screen.getByRole("dialog")).getByRole("button", { name: "Fechar caixa" }));

    const resultado = await screen.findByRole("dialog", { name: "Caixa fechado" });
    expect(within(resultado).getByText(/Aguardando aprovação/)).toBeVisible();
    expect(within(resultado).getByText(/um gerente precisa decidir/)).toBeVisible();
  });

  it("shows the refusal and keeps the counts when the close fails", async () => {
    stubRede({ ...aberto(), "POST /caixa/turnos/atual/fechar": json({ codigo: "invalido" }, { status: 400 }) });
    await renderEm("/salao/caixa");
    await userEvent.click(await screen.findByRole("button", { name: "Fechar caixa" }));
    const folha = await screen.findByRole("dialog", { name: "Fechar caixa" });
    await userEvent.type(within(folha).getByLabelText("Dinheiro contado"), "100");

    await userEvent.click(within(folha).getByRole("button", { name: "Fechar caixa" }));

    expect(await within(folha).findByRole("alert")).toHaveTextContent("Confira os valores contados");
    expect(within(folha).getByLabelText("Dinheiro contado")).toHaveValue(formatBRL(100));
  });
});

describe("blind count", () => {
  const cego = resumo({
    cego: true,
    esperadoCentavos: null,
    turno: turno({ esperadoOculto: true, recebidoDinheiroCentavos: 0 }),
    porMetodo: [{ metodoId: PIX, metodoNome: "Pix", esperadoCentavos: null }],
  });

  it("hides the expected value on the shift and in the close sheet, and reveals it only after sending", async () => {
    stubRede({
      ...aberto([], cego),
      "POST /caixa/turnos/atual/fechar": json(
        turno({
          fechadoEm: "2026-11-06T22:00:00Z",
          contadoCentavos: 9_000,
          esperadoCentavos: 12_000,
          diferencaCentavos: -3_000,
          diferencaTexto: `Falta ${brl(3_000)}`,
          situacao: "pendente",
        }),
      ),
    });
    await renderEm("/salao/caixa");

    const valores = (await screen.findByText("Esperado na gaveta")).closest("dl")!;
    expect(texto(valores)).toContain("Oculto até o fechamento");
    expect(screen.queryByText("Recebido em dinheiro")).toBeNull();
    expect(screen.getByText(/Conferência cega/)).toBeVisible();

    await userEvent.click(screen.getByRole("button", { name: "Fechar caixa" }));
    const folha = await screen.findByRole("dialog", { name: "Fechar caixa" });
    expect(within(folha).queryByText(/Esperado/)).toBeNull();
    await userEvent.type(within(folha).getByLabelText("Dinheiro contado"), "9000");
    expect(within(folha).queryByText(/Falta|Sobra|Confere/)).toBeNull();
    await userEvent.click(within(folha).getByRole("button", { name: "Fechar caixa" }));

    const resultado = await screen.findByRole("dialog", { name: "Caixa fechado" });
    expect(texto(resultado)).toContain(brl(12_000));
    expect(within(resultado).getByText(`Falta ${brl(3_000)}`)).toBeVisible();
  });
});

describe("pending differences for managers", () => {
  const pendente = (extra: Partial<Turno> = {}): Turno =>
    turno({
      id: "t9",
      operadorId: "o9",
      fechadoEm: "2026-11-05T22:00:00Z",
      contadoCentavos: 9_000,
      esperadoCentavos: 12_000,
      diferencaCentavos: -3_000,
      diferencaTexto: `Falta ${brl(3_000)}`,
      situacao: "pendente",
      ...extra,
    });
  const detalhe = {
    turno: pendente({ suprimentosCentavos: 0, sangriasCentavos: 0 }),
    contagens: [],
    movimentos: [],
    aprovacoes: [],
  };
  const gerente = (extra: Record<string, unknown> = {}) => ({
    ...aberto(),
    ...comPermissoes("caixa:ver-todos"),
    "GET /caixa/turnos/pendentes": json([pendente()]),
    "GET /caixa/turnos/t9": json(detalhe),
    ...extra,
  });

  it("lists them for who may see everyone's shifts", async () => {
    stubRede(gerente());
    await renderEm("/salao/caixa");
    expect(await screen.findByRole("heading", { level: 3, name: "Diferenças para aprovar" })).toBeVisible();
    expect(await screen.findByText(`Falta ${brl(3_000)}`)).toBeVisible();
  });

  it("does not show or ask for them to anyone else", async () => {
    const chamadas = stubRede(aberto());
    await renderEm("/salao/caixa");
    await screen.findByRole("heading", { level: 2, name: "Caixa aberto" });
    expect(screen.queryByRole("heading", { name: "Diferenças para aprovar" })).toBeNull();
    expect(chamadas.some((c) => c.path === "/caixa/turnos/pendentes")).toBe(false);
  });

  it("says there is nothing to decide when the list is empty", async () => {
    stubRede(gerente({ "GET /caixa/turnos/pendentes": json([]) }));
    await renderEm("/salao/caixa");
    expect(await screen.findByText("Nenhuma diferença esperando decisão.")).toBeVisible();
  });

  it("offers a retry when the list fails", async () => {
    stubRede(gerente({ "GET /caixa/turnos/pendentes": new Response(null, { status: 500 }) }));
    await renderEm("/salao/caixa");
    expect(await screen.findByText("Não foi possível carregar as diferenças pendentes.")).toBeVisible();
  });

  it("approves with a reason", async () => {
    const chamadas = stubRede(
      gerente({ "POST /caixa/turnos/t9/diferenca/aprovar": json(pendente({ situacao: "aprovada" })) }),
    );
    await renderEm("/salao/caixa");
    await userEvent.click(await screen.findByRole("button", { name: /Caixa de/ }));
    const folha = await screen.findByRole("dialog", { name: "Diferença do caixa" });
    expect(await within(folha).findByText(/Aguardando aprovação/)).toBeVisible();
    await userEvent.click(within(folha).getByRole("button", { name: "Aprovar" }));
    const decisao = await screen.findByRole("dialog", { name: "Aprovar diferença" });
    expect(within(decisao).getByRole("button", { name: "Aprovar" })).toBeDisabled();

    await userEvent.type(within(decisao).getByLabelText("Motivo"), "Troco dado errado, conferido em vídeo");
    await userEvent.click(within(decisao).getByRole("button", { name: "Aprovar" }));

    expect(await screen.findByText("Diferença aprovada.")).toBeVisible();
    expect(aoServidor(chamadas, "/diferenca/aprovar")?.body).toEqual({
      motivo: "Troco dado errado, conferido em vídeo",
    });
  });

  it("recounts with the new cash count and a reason, and says what became of the difference", async () => {
    const chamadas = stubRede(
      gerente({
        "POST /caixa/turnos/t9/diferenca/recontar": json(pendente({ situacao: "concluida", diferencaCentavos: 0 })),
      }),
    );
    await renderEm("/salao/caixa");
    await userEvent.click(await screen.findByRole("button", { name: /Caixa de/ }));
    await userEvent.click(await within(await screen.findByRole("dialog")).findByRole("button", { name: "Recontar" }));
    const folha = await screen.findByRole("dialog", { name: "Recontar caixa" });

    await userEvent.type(within(folha).getByLabelText("Dinheiro recontado"), "12000");
    await userEvent.type(within(folha).getByLabelText("Motivo"), "Contei de novo");
    await userEvent.click(within(folha).getByRole("button", { name: "Recontar" }));

    expect(await screen.findByText(/dentro da tolerância/)).toBeVisible();
    expect(aoServidor(chamadas, "/diferenca/recontar")?.body).toEqual({
      contadoCentavos: 12_000,
      motivo: "Contei de novo",
      contagens: [],
    });
  });

  it("refuses the person who closed the shift with a sentence, keeping the reason", async () => {
    stubRede(
      gerente({
        "POST /caixa/turnos/t9/diferenca/aprovar": json({ codigo: "autoaprovacao-nao-permitida" }, { status: 409 }),
      }),
    );
    await renderEm("/salao/caixa");
    await userEvent.click(await screen.findByRole("button", { name: /Caixa de/ }));
    await userEvent.click(await within(await screen.findByRole("dialog")).findByRole("button", { name: "Aprovar" }));
    const decisao = await screen.findByRole("dialog", { name: "Aprovar diferença" });
    await userEvent.type(within(decisao).getByLabelText("Motivo"), "ok");

    await userEvent.click(within(decisao).getByRole("button", { name: "Aprovar" }));

    expect(await within(decisao).findByRole("alert")).toHaveTextContent(
      "Quem fechou o caixa não decide a própria diferença",
    );
    expect(within(decisao).getByLabelText("Motivo")).toHaveValue("ok");
  });

  it("tells a 403 without a reason", async () => {
    stubRede(gerente({ "POST /caixa/turnos/t9/diferenca/aprovar": new Response(null, { status: 403 }) }));
    await renderEm("/salao/caixa");
    await userEvent.click(await screen.findByRole("button", { name: /Caixa de/ }));
    await userEvent.click(await within(await screen.findByRole("dialog")).findByRole("button", { name: "Aprovar" }));
    const decisao = await screen.findByRole("dialog", { name: "Aprovar diferença" });
    await userEvent.type(within(decisao).getByLabelText("Motivo"), "ok");

    await userEvent.click(within(decisao).getByRole("button", { name: "Aprovar" }));

    expect(await within(decisao).findByRole("alert")).toHaveTextContent(
      "Você não tem permissão para decidir esta diferença.",
    );
  });

  it("shows the manager's list even when their own register is closed", async () => {
    stubRede(
      gerente({
        "GET /caixa/turnos/atual/resumo": new Response(JSON.stringify({ codigo: "sem-turno-aberto" }), { status: 404 }),
      }),
    );
    await renderEm("/salao/caixa");

    expect(await screen.findByText("Caixa fechado. Abra o caixa para receber.")).toBeVisible();
    expect(await screen.findByRole("button", { name: /Caixa de/ })).toBeVisible();
  });
});
