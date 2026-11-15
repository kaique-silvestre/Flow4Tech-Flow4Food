import { act, screen, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { enfileirarRodada, lerFila } from "./features/fila/fila";
import { api } from "./features/sessao/sessao";
import { abasVisiveis, salaoTabs } from "./Shell";
import { json, jsonDoContrato, renderEm, sessaoJson, stubRede } from "./test-utils";

function larguraDaJanela(px: number) {
  Object.defineProperty(window, "innerWidth", { configurable: true, writable: true, value: px });
}

// The phone is the design size: every older test below runs on it; the bands have their own describes.
beforeEach(() => {
  larguraDaJanela(390);
  window.localStorage.removeItem("f4t-menu");
});

afterEach(() => {
  vi.unstubAllGlobals();
  larguraDaJanela(1024);
});

const pronta = {
  "POST /refresh": json(sessaoJson()),
  "GET /comandas": json([]),
  "GET /caixa/turnos/atual/resumo": new Response(null, { status: 404 }),
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

  it("names the company, the unit and the person of the session under the title", async () => {
    stubRede(pronta);

    await renderEm("/salao");

    const contexto = await screen.findByText(/Bar do Zé/);
    await vi.waitFor(() =>
      expect(contexto).toHaveTextContent("Bar do Zé · Unidade Centro · Sem turno aberto · Marina"),
    );
    expect(within(contexto).getByText("Marina")).toBeVisible();
  });

  it("leaves the context line out when the server does not answer it", async () => {
    stubRede({ ...pronta, "GET /sessao/contexto": new Response(null, { status: 500 }) });

    await renderEm("/salao");
    await screen.findByRole("heading", { level: 1, name: "Salão" });

    expect(screen.queryByText(/Bar do Zé/)).toBeNull();
  });

  it("switches the theme from the header, remembers it and offers the way back", async () => {
    window.localStorage.removeItem("f4t-tema");
    document.documentElement.dataset["theme"] = "light";
    stubRede(pronta);
    await renderEm("/salao");

    await userEvent.click(await screen.findByRole("button", { name: "Mudar para o tema escuro" }));

    expect(document.documentElement.dataset["theme"]).toBe("dark");
    expect(window.localStorage.getItem("f4t-tema")).toBe("escuro");
    await userEvent.click(screen.getByRole("button", { name: "Mudar para o tema claro" }));
    expect(document.documentElement.dataset["theme"]).toBe("light");
    delete document.documentElement.dataset["theme"];
    window.localStorage.removeItem("f4t-tema");
  });

  it("draws an icon above each tab label and keeps the label", async () => {
    stubRede(pronta);
    await renderEm("/salao");

    const comandas = await screen.findByRole("link", { name: "Comandas" });

    expect(comandas.querySelector("svg")).toHaveAttribute("aria-hidden", "true");
    expect(within(comandas).getByText("Comandas")).toBeVisible();
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
    expect(screen.getByRole("button", { name: "Mudar para o tema escuro" })).toHaveFocus();
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

describe("abasVisiveis (the permission filter of the navigation)", () => {
  const todas = [
    ...salaoTabs,
    { to: "/salao/cardapio", label: "Cardápio", icone: "book", permissao: "produto:gerenciar" },
  ] as const;

  it("keeps only the tabs the caller may use, in order", () => {
    const pode = (p: string) => p === "comanda:ver" || p === "produto:gerenciar";

    expect(abasVisiveis(todas, pode, true).map((t) => t.label)).toEqual(["Comandas", "Cardápio"]);
  });

  it("drops the tables tab when the unit has no tables, even with the permission", () => {
    const tudo = () => true;

    expect(abasVisiveis(todas, tudo, false).map((t) => t.label)).toEqual(["Comandas", "Caixa", "Cardápio"]);
    expect(abasVisiveis(todas, tudo, true).map((t) => t.label)).toEqual(["Comandas", "Mesas", "Caixa", "Cardápio"]);
  });

  it("shows nothing while nothing is known", () => {
    expect(abasVisiveis(todas, () => false, true)).toEqual([]);
  });
});

describe("the navigation follows the band of the window", () => {
  const aberto = {
    ...pronta,
    "GET /caixa/turnos/atual/resumo": jsonDoContrato("get", "/caixa/turnos/atual/resumo", 200, {
      turno: {
        id: "0197e2e0-0000-7000-8000-0000000000b1",
        unidadeId: "0197e2e0-0000-7000-8000-0000000000a1",
        operadorId: "0197e2e0-0000-7000-8000-0000000000c1",
        abertoEm: "2026-11-15T22:42:00Z",
        fundoCentavos: 10000,
        fechadoEm: null,
        contadoCentavos: null,
        esperadoCentavos: null,
        diferencaCentavos: null,
        diferencaTexto: null,
        recebidoDinheiroCentavos: 0,
        esperadoOculto: false,
        situacao: "aberto",
      },
      cego: false,
      suprimentosCentavos: 0,
      sangriasCentavos: 0,
      esperadoCentavos: null,
      porMetodo: [],
    }),
  };

  it("phone: bottom tab bar, no side menu", async () => {
    larguraDaJanela(390);
    stubRede(pronta);
    await renderEm("/salao");

    const nav = await screen.findByRole("navigation", { name: "Seções do salão" });

    expect(nav).toHaveAttribute("data-menu", "inferior");
    expect(screen.queryByRole("complementary")).toBeNull();
    expect(screen.queryByRole("button", { name: /menu/ })).toBeNull();
  });

  it("tablet: a 72 px rail whose links keep their names", async () => {
    larguraDaJanela(820);
    stubRede(pronta);
    await renderEm("/salao");

    const nav = await screen.findByRole("navigation", { name: "Seções do salão" });

    expect(nav).toHaveAttribute("data-menu", "trilho");
    expect(within(nav).getByRole("link", { name: "Comandas" })).toHaveAttribute("title", "Comandas");
    expect(within(nav).getByRole("link", { name: "Caixa" })).toBeVisible();
    expect(screen.getByRole("button", { name: "Expandir o menu" })).toHaveAttribute("aria-expanded", "false");
  });

  it("computer: the full menu, collapsible to the rail, and the choice is remembered", async () => {
    larguraDaJanela(1440);
    stubRede(pronta);
    await renderEm("/salao");
    const nav = await screen.findByRole("navigation", { name: "Seções do salão" });
    expect(nav).toHaveAttribute("data-menu", "completo");

    await userEvent.click(screen.getByRole("button", { name: "Recolher o menu" }));

    expect(nav).toHaveAttribute("data-menu", "trilho");
    expect(window.localStorage.getItem("f4t-menu")).toBe("trilho");
    await userEvent.click(screen.getByRole("button", { name: "Expandir o menu" }));
    expect(nav).toHaveAttribute("data-menu", "completo");
    expect(window.localStorage.getItem("f4t-menu")).toBe("completo");
  });

  it("a saved choice beats the band: the tablet can have the full menu", async () => {
    window.localStorage.setItem("f4t-menu", "completo");
    larguraDaJanela(820);
    stubRede(pronta);
    await renderEm("/salao");

    expect(await screen.findByRole("navigation", { name: "Seções do salão" })).toHaveAttribute("data-menu", "completo");
  });

  it("moves to the other navigation when the window is resized", async () => {
    larguraDaJanela(390);
    stubRede(pronta);
    await renderEm("/salao");
    await screen.findByRole("navigation", { name: "Seções do salão" });

    act(() => {
      larguraDaJanela(1440);
      window.dispatchEvent(new Event("resize"));
    });

    expect(await screen.findByRole("button", { name: "Recolher o menu" })).toBeVisible();
    expect(screen.getByRole("navigation", { name: "Seções do salão" })).toHaveAttribute("data-menu", "completo");
  });

  it("hides the tabs the session may not use on every band", async () => {
    for (const largura of [390, 820, 1440]) {
      larguraDaJanela(largura);
      stubRede({ ...pronta, "GET /sessao/permissoes": json({ permissoes: ["comanda:ver"] }) });
      const { unmount } = await renderEm("/salao");
      const nav = await screen.findByRole("navigation", { name: "Seções do salão" });

      expect(within(nav).getByRole("link", { name: "Comandas" })).toBeVisible();
      expect(within(nav).queryByRole("link", { name: "Caixa" })).toBeNull();
      unmount();
    }
  });

  it("the header names company, unit, shift and person on every band, and keeps the theme switch", async () => {
    for (const largura of [390, 820, 1440]) {
      larguraDaJanela(largura);
      stubRede(aberto);
      const { unmount } = await renderEm("/salao");

      const contexto = await screen.findByText(/Bar do Zé/);

      await vi.waitFor(() =>
        expect(contexto).toHaveTextContent(/Unidade Centro · Turno aberto desde \d{2}:\d{2} · Marina/),
      );
      expect(screen.getByRole("button", { name: "Mudar para o tema escuro" })).toBeVisible();
      unmount();
    }
  });

  it("leaves the shift out when the caller has no say on the caixa", async () => {
    stubRede({ ...pronta, "GET /sessao/permissoes": json({ permissoes: ["comanda:ver"] }) });
    await renderEm("/salao");

    const contexto = await screen.findByText(/Bar do Zé/);

    expect(contexto).toHaveTextContent("Bar do Zé · Unidade Centro · Marina");
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
