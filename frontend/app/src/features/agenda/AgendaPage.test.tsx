import { fireEvent, screen, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, describe, expect, it, vi } from "vitest";
import { json, renderEm, sessaoJson, stubRede, TODAS_AS_PERMISSOES } from "../../test-utils";
import type { AgendaDto } from "./logica";

afterEach(() => {
  vi.unstubAllGlobals();
});

const COM = [...TODAS_AS_PERMISSOES, "organizacao:configurar"];
const noite = { abre: "18:00", fecha: "23:00" };
const agenda = (extra: Partial<AgendaDto> = {}): AgendaDto => ({
  diaOperacional: "2026-11-17",
  fuso: "America/Sao_Paulo",
  virada: "05:00",
  semana: [null, noite, noite, noite, noite, { abre: "17:00", fecha: "03:00" }, { abre: "15:00", fecha: "03:00" }],
  excecoes: [{ dia: "2026-12-25", janela: null }],
  estendidoAte: null,
  janelaDeHoje: noite,
  abertoAgora: true,
  ...extra,
});
const base = (dto: AgendaDto = agenda(), extra: Record<string, Response> = {}) => ({
  "POST /refresh": json(sessaoJson()),
  "GET /comandas": json([]),
  "GET /sessao/permissoes": json({ permissoes: COM }),
  "GET /agenda": json(dto),
  ...extra,
});
const sem = () => new Response(null, { status: 204 });
const erro = (status: number, codigo: string) => json({ codigo }, { status });

describe("agenda de funcionamento", () => {
  it("shows today, the week from Monday and the exceptions as the server answered", async () => {
    stubRede(base());
    await renderEm("/gestao/agenda");

    expect(await screen.findByRole("heading", { name: "Agenda de funcionamento" })).toBeVisible();
    expect(screen.getByText(/Hoje, 17\/11\/2026 · terça/)).toBeVisible();
    expect(screen.getByText("Aberto agora")).toBeVisible();
    const semana = screen.getByRole("list", { name: "Dias da semana" });
    const dias = within(semana).getAllByRole("listitem");
    expect(dias).toHaveLength(7);
    expect(within(dias[0] as HTMLElement).getByRole("switch", { name: "Segunda: fechado" })).toHaveAttribute(
      "aria-checked",
      "false",
    );
    expect(within(dias[5] as HTMLElement).getByLabelText("Sábado: abre")).toHaveValue("17:00");
    expect(within(dias[5] as HTMLElement).getByLabelText("Sábado: fecha")).toHaveValue("03:00");
    const excecoes = screen.getByRole("list", { name: "Exceções cadastradas" });
    expect(within(excecoes).getByText(/25\/12\/2026 · sexta/)).toBeVisible();
    expect(within(excecoes).getByText("fechado")).toBeVisible();
  });

  it("explains what the server will block when the unit is out of hours, without deciding it", async () => {
    stubRede(base(agenda({ abertoAgora: false })));
    await renderEm("/gestao/agenda");

    expect(await screen.findByText("Fora do horário agora")).toBeVisible();
    expect(screen.getByText(/abrir comanda e lançar rodada serão recusados/)).toBeVisible();
  });

  it("shows the menu item to who may configure", async () => {
    stubRede(base());
    await renderEm("/gestao/agenda");
    expect(await screen.findByRole("link", { name: "Agenda" })).toHaveAttribute("aria-current", "page");
  });

  it("has no menu item for who may not, and the route says no", async () => {
    stubRede({ ...base(), "GET /sessao/permissoes": json({ permissoes: TODAS_AS_PERMISSOES }) });
    await renderEm("/gestao/agenda");
    expect(await screen.findByText("Você não tem permissão para isso.")).toBeVisible();
    expect(screen.queryByRole("link", { name: "Agenda" })).toBeNull();
  });

  it("answers a bare 403 as no permission", async () => {
    stubRede(base(agenda(), { "GET /agenda": new Response(null, { status: 403 }) }));
    await renderEm("/gestao/agenda");
    expect(await screen.findByText("Você não tem permissão para isso.")).toBeVisible();
  });

  it("saves the week: a closed day is null, an open day carries both times", async () => {
    const chamadas = stubRede(base(agenda(), { "PUT /agenda/semana": sem() }));
    await renderEm("/gestao/agenda");
    await userEvent.click(await screen.findByRole("switch", { name: "Segunda: fechado" }));
    fireEvent.change(screen.getByLabelText("Segunda: abre"), { target: { value: "12:00" } });
    fireEvent.change(screen.getByLabelText("Segunda: fecha"), { target: { value: "22:00" } });
    await userEvent.click(screen.getByRole("switch", { name: "Domingo: aberto" }));
    await userEvent.click(screen.getByRole("button", { name: "Salvar semana" }));

    expect(await screen.findByText("Semana salva.")).toBeVisible();
    const envio = chamadas.find((c) => c.method === "PUT" && c.path === "/agenda/semana");
    expect(envio?.body).toEqual({
      semana: [{ abre: "12:00", fecha: "22:00" }, noite, noite, noite, noite, { abre: "17:00", fecha: "03:00" }, null],
      motivo: null,
    });
  });

  it("checks the format before sending and sends nothing", async () => {
    const chamadas = stubRede(base());
    await renderEm("/gestao/agenda");
    await userEvent.click(await screen.findByRole("switch", { name: "Segunda: fechado" }));
    await userEvent.click(screen.getByRole("button", { name: "Salvar semana" }));

    expect(await screen.findByText("Informe a hora de abrir e a de fechar.")).toBeVisible();
    expect(chamadas.some((c) => c.method === "PUT")).toBe(false);
  });

  it("says why a window that crosses past the rollover was refused, with the server's rollover hour", async () => {
    stubRede(base(agenda(), { "PUT /agenda/semana": erro(400, "invalido") }));
    await renderEm("/gestao/agenda");
    await userEvent.click(await screen.findByRole("button", { name: "Salvar semana" }));

    expect(await screen.findByText(/Horário não aceito\. Abra às 05:00 ou depois/)).toBeVisible();
  });

  it("says when the organization locked the setting (409)", async () => {
    stubRede(base(agenda(), { "PUT /agenda/semana": erro(409, "travada") }));
    await renderEm("/gestao/agenda");
    await userEvent.click(await screen.findByRole("button", { name: "Salvar semana" }));
    expect(await screen.findByText(/organização travou/)).toBeVisible();
  });

  it("adds an exception: sends the existing ones plus the new, in date order", async () => {
    const chamadas = stubRede(base(agenda(), { "PUT /agenda/excecoes": sem() }));
    await renderEm("/gestao/agenda");
    await screen.findByRole("heading", { name: "Exceções por data" });
    fireEvent.change(screen.getByLabelText("Data"), { target: { value: "2026-11-20" } });
    await userEvent.click(screen.getByRole("switch", { name: "Fechado nesse dia" }));
    fireEvent.change(screen.getByLabelText("Abre às"), { target: { value: "14:00" } });
    fireEvent.change(screen.getByLabelText("Fecha às"), { target: { value: "03:00" } });
    await userEvent.type(screen.getByLabelText(/Motivo/), "Jogo no telão");
    await userEvent.click(screen.getByRole("button", { name: "Adicionar exceção" }));

    expect(await screen.findByText("Exceção salva.")).toBeVisible();
    const envio = chamadas.find((c) => c.method === "PUT" && c.path === "/agenda/excecoes");
    expect(envio?.body).toEqual({
      excecoes: [
        { dia: "2026-11-20", janela: { abre: "14:00", fecha: "03:00" } },
        { dia: "2026-12-25", janela: null },
      ],
      motivo: "Jogo no telão",
    });
  });

  it("removes an exception", async () => {
    const chamadas = stubRede(base(agenda(), { "PUT /agenda/excecoes": sem() }));
    await renderEm("/gestao/agenda");
    await userEvent.click(await screen.findByRole("button", { name: /Remover exceção de 25\/12\/2026/ }));

    expect(await screen.findByText("Exceção removida.")).toBeVisible();
    expect(chamadas.find((c) => c.path === "/agenda/excecoes")?.body).toEqual({ excecoes: [], motivo: null });
  });

  it("shows the server's message when there are too many exceptions", async () => {
    stubRede(base(agenda(), { "PUT /agenda/excecoes": erro(400, "excecoes-demais") }));
    await renderEm("/gestao/agenda");
    await screen.findByRole("heading", { name: "Exceções por data" });
    fireEvent.change(screen.getByLabelText("Data"), { target: { value: "2026-11-20" } });
    await userEvent.click(screen.getByRole("button", { name: "Adicionar exceção" }));
    expect(await screen.findByText(/até 10 exceções/)).toBeVisible();
  });

  it("holds the add form at ten exceptions", async () => {
    const dez = Array.from({ length: 10 }, (_, i) => ({ dia: `2026-12-${String(i + 10)}`, janela: null }));
    stubRede(base(agenda({ excecoes: dez })));
    await renderEm("/gestao/agenda");
    expect(await screen.findByText(/Limite atingido/)).toBeVisible();
    expect(screen.getByRole("button", { name: "Adicionar exceção" })).toBeDisabled();
  });

  it("extends today with the new closing time", async () => {
    const chamadas = stubRede(base(agenda(), { "POST /agenda/estender-hoje": sem() }));
    await renderEm("/gestao/agenda");
    await userEvent.click(await screen.findByRole("button", { name: "Estender hoje" }));
    fireEvent.change(screen.getByLabelText("Novo fechamento"), { target: { value: "01:00" } });
    await userEvent.click(screen.getByRole("button", { name: "Estender" }));

    expect(await screen.findByText("Hoje estendido.")).toBeVisible();
    expect(chamadas.find((c) => c.path === "/agenda/estender-hoje")?.body).toEqual({ fecha: "01:00", motivo: null });
  });

  it("says why today could not be extended (409) and keeps the form open", async () => {
    stubRede(base(agenda(), { "POST /agenda/estender-hoje": erro(409, "sem-horario-para-estender") }));
    await renderEm("/gestao/agenda");
    await userEvent.click(await screen.findByRole("button", { name: "Estender hoje" }));
    fireEvent.change(screen.getByLabelText("Novo fechamento"), { target: { value: "01:00" } });
    await userEvent.click(screen.getByRole("button", { name: "Estender" }));
    expect(await screen.findByText(/fechada ou aberta o dia todo/)).toBeVisible();
    expect(screen.getByLabelText("Novo fechamento")).toBeVisible();
  });

  it("shows the extension in force", async () => {
    stubRede(base(agenda({ estendidoAte: "01:00" })));
    await renderEm("/gestao/agenda");
    expect(await screen.findByText(/Estendido até 01:00/)).toBeVisible();
  });

  it("offers a retry when the agenda does not load", async () => {
    stubRede(base(agenda(), { "GET /agenda": new Response(null, { status: 500 }) }));
    await renderEm("/gestao/agenda");
    expect(await screen.findByText("Não foi possível carregar a agenda.")).toBeVisible();
    expect(screen.getByRole("button", { name: "Tentar novamente" })).toBeVisible();
  });
});
