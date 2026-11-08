import { screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, describe, expect, it, vi } from "vitest";
import { enfileirarRodada, lerFila } from "./features/fila/fila";
import { api } from "./features/sessao/sessao";
import { json, renderEm, sessaoJson, stubRede } from "./test-utils";

afterEach(() => {
  vi.unstubAllGlobals();
});

const pronta = {
  "POST /refresh": json(sessaoJson()),
  "GET /comandas": json([]),
  "GET /caixa/turnos/atual": new Response(null, { status: 404 }),
};

describe("salão shell (signed in)", () => {
  it("sends the root to /salao and renders the title and both tabs", async () => {
    stubRede(pronta);

    const { router } = await renderEm("/");

    expect(await screen.findByRole("heading", { level: 1, name: "Salão" })).toBeVisible();
    expect(router.state.location.pathname).toBe("/salao");
    expect(await screen.findByRole("navigation", { name: "Seções do salão" })).toBeVisible();
    expect(screen.getByRole("link", { name: "Comandas" })).toHaveAttribute("aria-current", "page");
    expect(screen.getByRole("link", { name: "Caixa" })).not.toHaveAttribute("aria-current");
  });

  it("switches sections through the tab bar", async () => {
    stubRede(pronta);
    const { router } = await renderEm("/salao");

    await userEvent.click(await screen.findByRole("link", { name: "Caixa" }));

    expect(router.state.location.pathname).toBe("/salao/caixa");
    expect(await screen.findByText("Caixa fechado. Abra o caixa para receber.")).toBeVisible();
    expect(screen.getByRole("link", { name: "Caixa" })).toHaveAttribute("aria-current", "page");
  });

  it("keeps the tab links reachable with the keyboard", async () => {
    stubRede(pronta);
    await renderEm("/salao");
    await screen.findByRole("heading", { level: 1, name: "Salão" });

    await userEvent.tab();
    expect(screen.getByRole("button", { name: "Sair" })).toHaveFocus();
    await userEvent.tab();
    expect(await screen.findByRole("button", { name: "Nova comanda" })).toHaveFocus();
    await userEvent.tab();
    expect(await screen.findByRole("link", { name: "Comandas" })).toHaveFocus();
    await userEvent.tab();
    expect(screen.getByRole("link", { name: "Caixa" })).toHaveFocus();
  });
});

/** Whatever a mounted screen re-requests after the clear holds no data of the account that left. */
const semDados = (qc: { getQueryCache: () => { getAll: () => { state: { data: unknown } }[] } }) =>
  qc
    .getQueryCache()
    .getAll()
    .every((q) => q.state.data === undefined);

describe("nothing of one account stays for the next on the same phone", () => {
  const fila = () =>
    enfileirarRodada({
      rodadaId: "r-1",
      comandaId: "c1",
      itens: [{ produtoId: "p1", quantidade: 1, observacao: null }],
      titulo: "Rodada · #7",
      detalhe: "1 item",
    });

  it("the Sair button empties the queue, its storage and the query cache", async () => {
    stubRede({ ...pronta, "POST /logout": new Response(null, { status: 204 }) });
    const { queryClient } = await renderEm("/salao");
    await screen.findByRole("heading", { level: 1, name: "Salão" });
    Object.defineProperty(window.navigator, "onLine", { configurable: true, value: false });
    fila();
    expect(lerFila()).toHaveLength(1);
    expect(queryClient.getQueryCache().getAll().length).toBeGreaterThan(0);

    await userEvent.click(screen.getByRole("button", { name: "Sair" }));

    await vi.waitFor(() => expect(lerFila()).toEqual([]));
    expect(window.localStorage.getItem("f4.fila.v1")).toBeNull();
    expect(semDados(queryClient)).toBe(true);
    Object.defineProperty(window.navigator, "onLine", { configurable: true, value: true });
  });

  it("an expired session (401, refresh refused) empties the queue and the cache and lands on sign-in", async () => {
    let refrescos = 0;
    stubRede({
      ...pronta,
      "POST /refresh": () => {
        refrescos += 1;
        return refrescos === 1 ? json(sessaoJson()) : new Response(null, { status: 401 });
      },
      "GET /unidades": new Response(null, { status: 401 }),
    });
    const { queryClient, router } = await renderEm("/salao");
    await screen.findByRole("heading", { level: 1, name: "Salão" });
    Object.defineProperty(window.navigator, "onLine", { configurable: true, value: false });
    fila();

    await api.GET("/unidades");

    await vi.waitFor(() => expect(lerFila()).toEqual([]));
    expect(semDados(queryClient)).toBe(true);
    await vi.waitFor(() => expect(router.state.location.pathname).toBe("/salao/entrar"));
    Object.defineProperty(window.navigator, "onLine", { configurable: true, value: true });
  });

  it("tells on the sign-in screen how many queued actions were discarded, and only once", async () => {
    stubRede({ ...pronta, "POST /logout": new Response(null, { status: 204 }), "POST /login": json(sessaoJson()) });
    const { router } = await renderEm("/salao");
    await screen.findByRole("heading", { level: 1, name: "Salão" });
    Object.defineProperty(window.navigator, "onLine", { configurable: true, value: false });
    fila();
    enfileirarRodada({
      rodadaId: "r-2",
      comandaId: "c1",
      itens: [{ produtoId: "p1", quantidade: 1, observacao: null }],
      titulo: "Rodada · #8",
      detalhe: "1 item",
    });

    await userEvent.click(screen.getByRole("button", { name: "Sair" }));

    expect(await screen.findByText("2 ações não enviadas foram descartadas.")).toBeVisible();
    await userEvent.type(screen.getByLabelText("Usuário ou e-mail"), "dono@bar.test");
    await userEvent.type(screen.getByLabelText("Senha"), "x");
    Object.defineProperty(window.navigator, "onLine", { configurable: true, value: true });
    await userEvent.click(screen.getByRole("button", { name: "Entrar" }));
    await vi.waitFor(() => expect(router.state.location.pathname).toBe("/salao"));
    await userEvent.click(await screen.findByRole("button", { name: "Sair" }));
    await screen.findByRole("heading", { level: 1, name: "Entrar" });
    expect(screen.queryByText(/descartadas/)).toBeNull();
  });
});
