import { screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, describe, expect, it, vi } from "vitest";
import { type Chamada, json, renderEm, sessaoJson, stubRede } from "../../test-utils";

afterEach(() => {
  vi.unstubAllGlobals();
});

const minutosAtras = (m: number) => new Date(Date.now() - m * 60_000).toISOString();
const comanda = (n: number, extra: Record<string, unknown> = {}) => ({
  id: `c${n}`,
  unidadeId: "u1",
  numero: n,
  numeroTexto: `#${n}`,
  identificacao: `Comanda ${n}`,
  mesaId: null,
  canal: "salao",
  garcomId: "g1",
  pessoas: null,
  estado: "aberta",
  taxaServicoPercentual: 10,
  taxaServicoRemovida: false,
  versao: 1,
  abertaEm: minutosAtras(12),
  garcomNome: "Rafael",
  consumoCentavos: 11700,
  ultimaAtividadeEm: minutosAtras(5),
  ...extra,
});
const central = (comandas: unknown[], total = comandas.length) => json({ comandas, total });
const base = { "POST /refresh": json(sessaoJson()), "GET /mesas": json([]) };
/** The URLs the screen asked the central with (the stub keeps only the path). */
const urlsDaCentral = () =>
  vi
    .mocked(fetch)
    .mock.calls.map(([entrada]) => (entrada as Request).url)
    .filter((url) => new URL(url).pathname === "/comandas/central");
const consultas = (chamadas: Chamada[]) => chamadas.filter((c) => c.path === "/comandas/central");

describe("central de comandas", () => {
  it("lists each open comanda with number, table, waiter, value, time open and the stalled alert, and the total", async () => {
    stubRede({
      ...base,
      "GET /comandas/central": central([
        comanda(7, { identificacao: "Ana", garcomNome: "Marina", consumoCentavos: 123456 }),
        comanda(8, { identificacao: "Balcão 1", abertaEm: minutosAtras(135), ultimaAtividadeEm: minutosAtras(90) }),
      ]),
    });

    await renderEm("/gestao/comandas");

    const lista = await screen.findByRole("list", { name: "Comandas abertas" });
    expect(screen.getByRole("status")).toHaveTextContent("2 comandas abertas");
    const ana = within(lista).getByRole("link", { name: /Ana/ });
    expect(ana).toHaveTextContent(/R\$\s*1\.234,56/);
    expect(ana).toHaveTextContent("12 min aberta · Marina");
    const parada = within(lista).getByRole("link", { name: /Balcão 1/ });
    expect(parada).toHaveTextContent("2h 15min aberta · Rafael");
    expect(parada).toHaveTextContent(/Parada/);
  });

  it("shows the table view from the address with the same data in columns", async () => {
    stubRede({
      ...base,
      "GET /comandas/central": central([comanda(7, { identificacao: "Ana", garcomNome: "Marina" })]),
    });

    await renderEm("/gestao/comandas?vista=tabela");

    const tabela = await screen.findByRole("table", { name: "Comandas abertas" });
    expect(
      within(tabela)
        .getAllByRole("columnheader")
        .map((c) => c.textContent),
    ).toEqual(["Comanda", "Mesa", "Garçom", "Valor", "Tempo aberta", "Situação"]);
    const linha = within(tabela).getByRole("row", { name: /#7/ });
    expect(linha).toHaveTextContent("Marina");
    expect(linha).toHaveTextContent("12 min");
    expect(screen.getByRole("button", { name: "Tabela" })).toHaveAttribute("aria-pressed", "true");
  });

  it("switches between cards and table, keeping the filters in the address", async () => {
    stubRede({ ...base, "GET /comandas/central": central([comanda(7)]) });
    const { router } = await renderEm("/gestao/comandas?garcom=g1");
    await screen.findByRole("list", { name: "Comandas abertas" });

    await userEvent.click(screen.getByRole("button", { name: "Tabela" }));

    expect(await screen.findByRole("table")).toBeVisible();
    expect(router.state.location.search).toEqual({ garcom: "g1", vista: "tabela" });
  });

  it("shows the new total when a waiter is chosen, and keeps the other waiters choosable", async () => {
    let n = 0;
    stubRede({
      ...base,
      "GET /comandas/central": () =>
        n++ === 0
          ? central([comanda(7, { garcomId: "g2", garcomNome: "Marina" }), comanda(8)])
          : central([comanda(8)], 1),
    });

    await renderEm("/gestao/comandas");
    expect(await screen.findByText("2 comandas abertas")).toBeVisible();

    await userEvent.selectOptions(screen.getByRole("combobox", { name: "Garçom" }), "Rafael");

    expect(await screen.findByText("1 comanda aberta")).toBeVisible();
    expect(
      within(screen.getByRole("combobox", { name: "Garçom" })).getByRole("option", { name: "Marina" }),
    ).toBeVisible();
    expect(urlsDaCentral().at(-1)).toContain("garcomId=g1");
  });

  it("asks the server with only the filled filters, from the address", async () => {
    stubRede({ ...base, "GET /comandas/central": central([comanda(12)]) });

    await renderEm("/gestao/comandas?garcom=g1&tempo=45&numero=12&vista=tabela");
    await screen.findByRole("table");

    const url = new URL(urlsDaCentral()[0] ?? "");
    expect(Object.fromEntries(url.searchParams)).toEqual({ garcomId: "g1", tempoMinimoMinutos: "45", numero: "12" });
  });

  it("applies the minutes and the number typed when the person presses Enter", async () => {
    stubRede({ ...base, "GET /comandas/central": central([comanda(12)]) });
    const { router } = await renderEm("/gestao/comandas");
    await screen.findByRole("list", { name: "Comandas abertas" });

    await userEvent.type(screen.getByRole("textbox", { name: "Aberta há (min)" }), "45x");
    await userEvent.type(screen.getByRole("textbox", { name: "Número" }), "12{Enter}");

    await waitFor(() => expect(router.state.location.search).toEqual({ tempo: "45", numero: "12" }));
  });

  it("says so when nothing is open, and offers to clear filters when filtered", async () => {
    stubRede({ ...base, "GET /comandas/central": central([]) });
    const vazio = await renderEm("/gestao/comandas");
    expect(await screen.findByText("Nenhuma comanda aberta.")).toBeVisible();
    expect(screen.queryByRole("button", { name: "Limpar filtros" })).toBeNull();
    vazio.unmount();

    stubRede({ ...base, "GET /comandas/central": central([]) });
    const { router } = await renderEm("/gestao/comandas?garcom=g1");
    expect(await screen.findByText("Nenhuma comanda aberta com estes filtros.")).toBeVisible();

    await userEvent.click(screen.getByRole("button", { name: "Limpar filtros" }));

    await waitFor(() => expect(router.state.location.search).toEqual({}));
  });

  it("shows the loading skeleton, then an error with a retry", async () => {
    let tentativas = 0;
    stubRede({
      ...base,
      "GET /comandas/central": () => (tentativas++ === 0 ? new Response(null, { status: 500 }) : central([comanda(7)])),
    });

    await renderEm("/gestao/comandas");

    expect(await screen.findByText("Não foi possível carregar as comandas.")).toBeVisible();
    await userEvent.click(screen.getByRole("button", { name: "Tentar novamente" }));
    expect(await screen.findByRole("list", { name: "Comandas abertas" })).toBeVisible();
  });

  it("tells a caller without comanda:ver that they may not see it, and asks the server for nothing", async () => {
    const chamadas = stubRede({ ...base, "GET /sessao/permissoes": json({ permissoes: ["caixa:ver"] }) });

    await renderEm("/gestao/comandas");

    expect(await screen.findByText("Você não tem permissão para ver as comandas.")).toBeVisible();
    expect(consultas(chamadas)).toHaveLength(0);
  });

  it("offers the Central item in the side menu only to who has comanda:ver", async () => {
    stubRede({ ...base, "GET /comandas/central": central([]) });
    const com = await renderEm("/gestao/comandas");
    expect(await screen.findByRole("link", { name: "Central" })).toHaveAttribute("aria-current", "page");
    com.unmount();

    stubRede({ ...base, "GET /sessao/permissoes": json({ permissoes: ["caixa:ver"] }), "GET /comandas": json([]) });
    await renderEm("/salao/caixa");
    await screen.findByRole("link", { name: "Caixa" });
    expect(screen.queryByRole("link", { name: "Central" })).toBeNull();
  });
});
