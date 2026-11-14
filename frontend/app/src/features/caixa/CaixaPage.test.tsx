import type { components } from "@flow4tech/api-client";
import { formatBRL } from "@flow4tech/design-system";
import { screen, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, describe, expect, it, vi } from "vitest";
import { json, jsonDoContrato, renderEm, sessaoJson, stubRede, TODAS_AS_PERMISSOES } from "../../test-utils";

afterEach(() => {
  vi.unstubAllGlobals();
});

const pronta = { "POST /refresh": json(sessaoJson()), "GET /comandas": json([]) };
const turno = (extra: Partial<components["schemas"]["TurnoDto"]> = {}): components["schemas"]["TurnoDto"] => ({
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
const resumo = (
  t: components["schemas"]["TurnoDto"],
  extra: Partial<components["schemas"]["ResumoDoTurnoDto"]> = {},
): components["schemas"]["ResumoDoTurnoDto"] => ({
  turno: t,
  cego: false,
  suprimentosCentavos: 0,
  sangriasCentavos: 0,
  esperadoCentavos: Number(t.fundoCentavos) + Number(t.recebidoDinheiroCentavos),
  porMetodo: [],
  ...extra,
});
const abertoComMovimentos = (t = turno()) => ({
  "GET /caixa/turnos/atual/resumo": json(resumo(t)),
  "GET /caixa/turnos/atual/movimentos": json([]),
});
const MOVS = { "GET /caixa/turnos/atual/movimentos": json([]) };
const semTurno = new Response(JSON.stringify({ codigo: "sem-turno-aberto" }), { status: 404 });

describe("caixa screen", () => {
  it("shows the closed state with the float field, explaining that the float is not a sale", async () => {
    stubRede({ ...pronta, "GET /caixa/turnos/atual/resumo": semTurno });

    await renderEm("/salao/caixa");

    expect(await screen.findByText("Caixa fechado. Abra o caixa para receber.")).toBeVisible();
    expect(screen.getByLabelText("Fundo de troco")).toBeVisible();
    expect(screen.getByText(/Não é venda/)).toBeVisible();
    expect(screen.getByRole("button", { name: "Abrir caixa" })).toBeDisabled();
  });

  it("opens the shift with the float typed in cents and then shows it", async () => {
    let aberto = false;
    const chamadas = stubRede({
      ...pronta,
      ...MOVS,
      "GET /caixa/turnos/atual/resumo": () =>
        aberto ? json(resumo(turno({ recebidoDinheiroCentavos: 0 }))) : semTurno.clone(),
      "POST /caixa/turnos": () => {
        aberto = true;
        return json(turno({ recebidoDinheiroCentavos: 0 }), { status: 201 });
      },
    });
    await renderEm("/salao/caixa");

    await userEvent.type(await screen.findByLabelText("Fundo de troco"), "10000");
    expect(screen.getByLabelText("Fundo de troco")).toHaveValue(formatBRL(10_000));
    await userEvent.click(screen.getByRole("button", { name: "Abrir caixa" }));

    expect(await screen.findByRole("heading", { level: 2, name: "Caixa aberto" })).toBeVisible();
    expect(chamadas.find((c) => c.path === "/caixa/turnos" && c.method === "POST")?.body).toEqual({
      fundoCentavos: 10_000,
    });
  });

  it("lets a zero float open the shift", async () => {
    const chamadas = stubRede({
      ...pronta,
      "GET /caixa/turnos/atual/resumo": semTurno,
      "POST /caixa/turnos": jsonDoContrato("post", "/caixa/turnos", 201, turno()),
    });
    await renderEm("/salao/caixa");

    await userEvent.type(await screen.findByLabelText("Fundo de troco"), "0");
    await userEvent.click(screen.getByRole("button", { name: "Abrir caixa" }));

    await vi.waitFor(() =>
      expect(chamadas.find((c) => c.path === "/caixa/turnos" && c.method === "POST")?.body).toEqual({
        fundoCentavos: 0,
      }),
    );
  });

  it("shows a second open as an error the user can act on", async () => {
    stubRede({
      ...pronta,
      "GET /caixa/turnos/atual/resumo": semTurno,
      "POST /caixa/turnos": json({ codigo: "turno-ja-aberto" }, { status: 409 }),
    });
    await renderEm("/salao/caixa");
    await userEvent.type(await screen.findByLabelText("Fundo de troco"), "5000");

    await userEvent.click(screen.getByRole("button", { name: "Abrir caixa" }));

    const aviso = await screen.findByRole("alert");
    expect(aviso).toHaveTextContent("Você já tem um caixa aberto nesta unidade.");
    expect(within(aviso).getByRole("button", { name: "Atualizar" })).toBeVisible();
  });

  it("shows the open shift: opened at, float, received and the expected cash; the float is not a sale", async () => {
    stubRede({ ...pronta, ...abertoComMovimentos() });

    await renderEm("/salao/caixa");

    expect(await screen.findByRole("heading", { level: 2, name: "Caixa aberto" })).toBeVisible();
    expect(screen.getByText(/^Aberto às \d{2}:\d{2}$/)).toBeVisible();
    const valores = screen.getByText("Fundo de troco").closest("dl")!;
    expect(valores.textContent).toContain(formatBRL(10_000));
    expect(valores.textContent).toContain(formatBRL(2_750));
    expect(within(valores).getByText("Esperado na gaveta").parentElement!.textContent).toContain(formatBRL(12_750));
    expect(screen.getByText("O fundo não é venda: ele só entra na conta do esperado.")).toBeVisible();
  });

  it.each([
    [0, "Confere", "success"],
    [-500, "Falta R$ 5,00", "alert"],
    [1_200, "Sobra R$ 12,00", "warning"],
  ])("closes with the count and tells the result in words: difference %i", async (diferenca, palavra, tom) => {
    stubRede({
      ...pronta,
      ...abertoComMovimentos(),
      "POST /caixa/turnos/atual/fechar": (c) =>
        json(
          turno({
            fechadoEm: "2026-11-06T22:00:00Z",
            contadoCentavos: (c.body as { contadoCentavos: number }).contadoCentavos,
            esperadoCentavos: 12_750,
            diferencaCentavos: diferenca,
            diferencaTexto: palavra,
          }),
          { status: 200 },
        ),
    });
    await renderEm("/salao/caixa");
    await userEvent.click(await screen.findByRole("button", { name: "Fechar caixa" }));
    const folha = await screen.findByRole("dialog", { name: "Fechar caixa" });
    expect(within(folha).getByRole("button", { name: "Fechar caixa" })).toBeDisabled();

    await userEvent.type(within(folha).getByLabelText("Dinheiro contado"), "12750");
    await userEvent.click(within(folha).getByRole("button", { name: "Fechar caixa" }));

    const resultado = await screen.findByRole("dialog", { name: "Caixa fechado" });
    expect(within(resultado).getByText(palavra)).toBeVisible();
    expect(resultado.querySelector(".f4-badge")).toHaveAttribute("data-tone", tom);
    expect(resultado.textContent).toContain(formatBRL(12_750));
  });

  it("accepts a zero count and sends it", async () => {
    const chamadas = stubRede({
      ...pronta,
      ...abertoComMovimentos(turno({ recebidoDinheiroCentavos: 0 })),
      "POST /caixa/turnos/atual/fechar": json(
        turno({
          contadoCentavos: 0,
          esperadoCentavos: 10_000,
          diferencaCentavos: -10_000,
          diferencaTexto: "Falta R$ 100,00",
        }),
      ),
    });
    await renderEm("/salao/caixa");
    await userEvent.click(await screen.findByRole("button", { name: "Fechar caixa" }));
    const folha = await screen.findByRole("dialog");

    await userEvent.type(within(folha).getByLabelText("Dinheiro contado"), "0");
    await userEvent.click(within(folha).getByRole("button", { name: "Fechar caixa" }));

    expect(await screen.findByText("Falta R$ 100,00")).toBeVisible();
    expect(chamadas.find((c) => c.path === "/caixa/turnos/atual/fechar")?.body).toEqual({ contadoCentavos: 0 });
  });

  it("goes back to the closed state after concluding", async () => {
    let fechado = false;
    stubRede({
      ...pronta,
      ...MOVS,
      "GET /caixa/turnos/atual/resumo": () => (fechado ? semTurno.clone() : json(resumo(turno()))),
      "POST /caixa/turnos/atual/fechar": () => {
        fechado = true;
        return json(
          turno({ contadoCentavos: 12_750, esperadoCentavos: 12_750, diferencaCentavos: 0, diferencaTexto: "Confere" }),
        );
      },
    });
    await renderEm("/salao/caixa");
    await userEvent.click(await screen.findByRole("button", { name: "Fechar caixa" }));
    await userEvent.type(within(await screen.findByRole("dialog")).getByLabelText("Dinheiro contado"), "12750");
    await userEvent.click(within(screen.getByRole("dialog")).getByRole("button", { name: "Fechar caixa" }));

    await userEvent.click(await screen.findByRole("button", { name: "Concluir" }));

    expect(await screen.findByText("Caixa fechado. Abra o caixa para receber.")).toBeVisible();
  });

  it("shows a skeleton while loading and a retry when the load fails", async () => {
    let falhar = true;
    stubRede({
      ...pronta,
      ...MOVS,
      "GET /caixa/turnos/atual/resumo": () => (falhar ? new Response(null, { status: 500 }) : json(resumo(turno()))),
    });
    await renderEm("/salao/caixa");

    expect(await screen.findByText("Não foi possível carregar o caixa.")).toBeVisible();
    falhar = false;
    await userEvent.click(screen.getByRole("button", { name: "Tentar novamente" }));

    expect(await screen.findByRole("heading", { level: 2, name: "Caixa aberto" })).toBeVisible();
  });

  it("hides, not disables, the actions the caller lacks", async () => {
    stubRede({
      ...pronta,
      "GET /sessao/permissoes": json({ permissoes: ["caixa:ver", "comanda:ver"] }),
      "GET /caixa/turnos/atual/resumo": semTurno,
    });
    await renderEm("/salao/caixa");

    expect(await screen.findByText("Caixa fechado. Abra o caixa para receber.")).toBeVisible();
    expect(screen.queryByLabelText("Fundo de troco")).toBeNull();
    expect(screen.queryByRole("button", { name: "Abrir caixa" })).toBeNull();

    stubRede({
      ...pronta,
      "GET /sessao/permissoes": json({ permissoes: ["caixa:ver", "comanda:ver"] }),
      ...abertoComMovimentos(),
    });
    await renderEm("/salao/caixa");
    expect(await screen.findByRole("heading", { level: 2, name: "Caixa aberto" })).toBeVisible();
    expect(screen.queryByRole("button", { name: "Fechar caixa" })).toBeNull();
  });

  it("says there is no permission when the server refuses to show the shift", async () => {
    stubRede({ ...pronta, "GET /caixa/turnos/atual/resumo": new Response(null, { status: 403 }) });

    await renderEm("/salao/caixa");

    expect(await screen.findByText("Você não tem permissão para ver o caixa.")).toBeVisible();
  });
});

describe("tab bar by permission", () => {
  it("shows only the tabs the caller may use", async () => {
    stubRede({ ...pronta, "GET /sessao/permissoes": json({ permissoes: ["comanda:ver"] }) });

    await renderEm("/salao");

    expect(await screen.findByRole("link", { name: "Comandas" })).toBeVisible();
    expect(screen.queryByRole("link", { name: "Caixa" })).toBeNull();
  });

  it("tells a caller without comanda:ver that the list is not theirs, and does not ask for it", async () => {
    const chamadas = stubRede({ ...pronta, "GET /sessao/permissoes": json({ permissoes: ["caixa:ver"] }) });

    await renderEm("/salao");

    expect(await screen.findByText("Você não tem permissão para ver as comandas.")).toBeVisible();
    expect(chamadas.some((c) => c.path === "/comandas")).toBe(false);
  });
});

describe("test permissions", () => {
  it("default to the three cash register permissions of the original screen", () => {
    expect(TODAS_AS_PERMISSOES).toContain("caixa:abrir");
  });
});
