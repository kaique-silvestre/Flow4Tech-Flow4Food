import { act, screen, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, describe, expect, it, vi } from "vitest";
import { json, renderEm, sessaoJson, stubRede } from "../../test-utils";
import { acharMesaPorNumero, combinaBusca, nomeExibido, paraComanda, tempoDecorrido } from "./tipos";

afterEach(() => {
  vi.unstubAllGlobals();
  vi.useRealTimers();
});

const base = { "POST /refresh": json(sessaoJson()) };
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
  abertaEm: new Date(Date.now() - 12 * 60_000).toISOString(),
  ...extra,
});
const mesas = [
  { id: "m1", unidadeId: "u1", identificacao: "1", capacidade: null, ativa: true, status: "livre" },
  { id: "m4", unidadeId: "u1", identificacao: "4", capacidade: null, ativa: true, status: "livre" },
  { id: "m12", unidadeId: "u1", identificacao: "12", capacidade: null, ativa: true, status: "ocupada" },
];

describe("comanda list", () => {
  it("shows the empty state and the single accent action", async () => {
    stubRede({ ...base, "GET /comandas": json([]), "GET /mesas": json(mesas) });

    await renderEm("/salao");

    expect(await screen.findByText("Nenhuma comanda aberta. Toque em Nova comanda.")).toBeVisible();
    expect(screen.getByRole("button", { name: "Nova comanda" })).toHaveAttribute("data-variant", "primary");
    expect(screen.getAllByRole("button").filter((b) => b.getAttribute("data-variant") === "primary")).toHaveLength(1);
  });

  it("lists the open comandas with number, name, status badge and elapsed time", async () => {
    stubRede({ ...base, "GET /comandas": json([comanda(7, { identificacao: "Aniversário da Ana" })]), "GET /mesas": json([]) });

    await renderEm("/salao");

    const linha = await screen.findByRole("link", { name: /#7/ });
    expect(within(linha).getByText("Aniversário da Ana")).toBeVisible();
    expect(within(linha).getByText("há 12 min")).toBeVisible();
    expect(within(linha).getByText("Aberta")).toBeVisible();
    expect(screen.getByRole("heading", { level: 2, name: "Minhas comandas" })).toBeVisible();
  });

  it("calls it Comandas abertas for who may see the others'", async () => {
    stubRede({ ...base, "GET /sessao/permissoes": json({ permissoes: ["comanda:ver", "comanda:ver-comandas-de-outros"] }), "GET /comandas": json([comanda(1)]) });

    await renderEm("/salao");

    expect(await screen.findByRole("heading", { level: 2, name: "Comandas abertas" })).toBeVisible();
    expect(screen.queryByRole("button", { name: "Nova comanda" })).toBeNull();
  });

  it("searches by number and by name, ignoring accents, and says so when nothing matches", async () => {
    stubRede({ ...base, "GET /comandas": json([comanda(7, { identificacao: "Aniversário" }), comanda(12, { identificacao: "Mesa dos primos" })]), "GET /mesas": json([]) });
    await renderEm("/salao");
    const busca = await screen.findByLabelText("Buscar comanda");

    await userEvent.type(busca, "aniversario");
    expect(screen.getByRole("link", { name: /#7/ })).toBeVisible();
    expect(screen.queryByRole("link", { name: /#12/ })).toBeNull();

    await userEvent.clear(busca);
    await userEvent.type(busca, "#12");
    expect(screen.getByRole("link", { name: /#12/ })).toBeVisible();

    await userEvent.clear(busca);
    await userEvent.type(busca, "xyz");
    expect(screen.getByText("Nada com “xyz”. Tente o número da comanda.")).toBeVisible();
  });

  it("shows the offline notice and keeps the list that was loaded", async () => {
    stubRede({ ...base, "GET /comandas": json([comanda(3)]), "GET /mesas": json([]) });
    await renderEm("/salao");
    await screen.findByRole("link", { name: /#3/ });

    await act(async () => {
      window.dispatchEvent(new Event("offline"));
    });

    expect(screen.getByText("Sem conexão. A lista pode estar desatualizada.")).toBeVisible();
    expect(screen.getByRole("link", { name: /#3/ })).toBeVisible();
    await act(async () => {
      window.dispatchEvent(new Event("online"));
    });
    expect(screen.queryByText("Sem conexão. A lista pode estar desatualizada.")).toBeNull();
  });

  it("shows a skeleton while loading and the error banner with retry", async () => {
    let falhar = true;
    stubRede({ ...base, "GET /comandas": () => (falhar ? new Response(null, { status: 500 }) : json([comanda(1)])), "GET /mesas": json([]) });
    await renderEm("/salao");

    expect(await screen.findByText("Não foi possível carregar as comandas.")).toBeVisible();
    falhar = false;
    await userEvent.click(screen.getByRole("button", { name: "Tentar novamente" }));

    expect(await screen.findByRole("link", { name: /#1/ })).toBeVisible();
  });

  it("opens the comanda screen from a row", async () => {
    stubRede({ ...base, "GET /comandas": json([comanda(5)]), "GET /mesas": json([]) });
    const { router } = await renderEm("/salao");

    await userEvent.click(await screen.findByRole("link", { name: /#5/ }));

    expect(router.state.location.pathname).toBe("/salao/comanda/c5");
    expect(await screen.findByRole("heading", { level: 2, name: "#5 · Comanda 5" })).toBeVisible();
  });
});

describe("new comanda", () => {
  /** The keys of the pad: a table chip can carry the same label as a key ("4"), so taps go through the pad. */
  const teclado = (folha: HTMLElement) => within(within(folha).getByRole("group", { name: "Teclado numérico" }));

  async function abrirFolha() {
    await userEvent.click(await screen.findByRole("button", { name: "Nova comanda" }));
    return screen.findByRole("dialog", { name: "Nova comanda" });
  }

  it("opens a free table with two taps and lands on the comanda", async () => {
    const chamadas = stubRede({
      ...base,
      "GET /comandas": json([]),
      "GET /mesas": json(mesas),
      "POST /comandas": json(comanda(1, { id: "novo", mesaId: "m4", identificacao: "4" }), { status: 201 }),
    });
    const { router } = await renderEm("/salao");
    const folha = await abrirFolha();
    expect(within(folha).getByRole("button", { name: "OK" })).toBeDisabled();

    await userEvent.click(teclado(folha).getByRole("button", { name: "4" }));
    expect(within(folha).getByLabelText("Mesa 4")).toBeVisible();
    await userEvent.click(within(folha).getByRole("button", { name: "OK" }));

    await vi.waitFor(() => expect(router.state.location.pathname).toBe("/salao/comanda/novo"));
    expect(chamadas.find((c) => c.path === "/comandas" && c.method === "POST")?.body).toEqual({ identificacao: null, mesaId: "m4", pessoas: null });
  });

  it("offers the free tables as chips and opens the chosen one", async () => {
    const chamadas = stubRede({ ...base, "GET /comandas": json([]), "GET /mesas": json(mesas), "POST /comandas": json(comanda(1, { id: "novo" }), { status: 201 }) });
    await renderEm("/salao");
    const folha = await abrirFolha();

    const chips = within(folha).getByRole("group", { name: "Mesas livres" });
    expect(within(chips).getAllByRole("button").map((b) => b.textContent)).toEqual(["1", "4"]);
    await userEvent.click(within(chips).getByRole("button", { name: "1" }));
    expect(within(chips).getByRole("button", { name: "1" })).toHaveAttribute("aria-pressed", "true");
    await userEvent.click(within(folha).getByRole("button", { name: "OK" }));

    await vi.waitFor(() => expect(chamadas.find((c) => c.path === "/comandas" && c.method === "POST")?.body).toMatchObject({ mesaId: "m1" }));
  });

  it("says a table does not exist without calling the server", async () => {
    const chamadas = stubRede({ ...base, "GET /comandas": json([]), "GET /mesas": json(mesas) });
    await renderEm("/salao");
    const folha = await abrirFolha();

    await userEvent.click(within(folha).getByRole("button", { name: "9" }));
    await userEvent.click(within(folha).getByRole("button", { name: "OK" }));

    expect(await screen.findByText("Não existe a mesa 9.")).toBeVisible();
    expect(chamadas.some((c) => c.path === "/comandas" && c.method === "POST")).toBe(false);
  });

  it("asks before opening another comanda on an occupied table, and lets the waiter see the existing one", async () => {
    const chamadas = stubRede({
      ...base,
      "GET /comandas": json([comanda(3, { id: "c3", mesaId: "m12", identificacao: "12" })]),
      "GET /mesas": json(mesas),
      "POST /comandas": json(comanda(9, { id: "outra", mesaId: "m12" }), { status: 201 }),
    });
    const { router } = await renderEm("/salao");
    const folha = await abrirFolha();
    await userEvent.click(teclado(folha).getByRole("button", { name: "1" }));
    await userEvent.click(teclado(folha).getByRole("button", { name: "2" }));

    await userEvent.click(within(folha).getByRole("button", { name: "OK" }));

    const aviso = await screen.findByRole("dialog", { name: "Mesa 12 já está ocupada" });
    expect(chamadas.some((c) => c.path === "/comandas" && c.method === "POST")).toBe(false);
    expect(within(aviso).getByRole("button", { name: "#3 · 12" })).toBeVisible();
    await userEvent.click(within(aviso).getByRole("button", { name: "Abrir outra comanda" }));

    await vi.waitFor(() => expect(router.state.location.pathname).toBe("/salao/comanda/outra"));
    expect(chamadas.find((c) => c.path === "/comandas" && c.method === "POST")?.body).toMatchObject({ mesaId: "m12" });
  });

  it("goes to the existing comanda of an occupied table", async () => {
    stubRede({ ...base, "GET /comandas": json([comanda(3, { id: "c3", mesaId: "m12", identificacao: "12" })]), "GET /mesas": json(mesas) });
    const { router } = await renderEm("/salao");
    const folha = await abrirFolha();
    await userEvent.click(teclado(folha).getByRole("button", { name: "1" }));
    await userEvent.click(teclado(folha).getByRole("button", { name: "2" }));
    await userEvent.click(within(folha).getByRole("button", { name: "OK" }));

    await userEvent.click(await screen.findByRole("button", { name: "#3 · 12" }));

    await vi.waitFor(() => expect(router.state.location.pathname).toBe("/salao/comanda/c3"));
  });

  it("names the comanda by number when the unit works without tables", async () => {
    const chamadas = stubRede({ ...base, "GET /comandas": json([]), "GET /mesas": json([]), "POST /comandas": json(comanda(1, { id: "novo", identificacao: "Comanda 7" }), { status: 201 }) });
    await renderEm("/salao");
    const folha = await abrirFolha();
    expect(within(folha).getByLabelText("Comanda em branco")).toBeVisible();
    expect(within(folha).queryByRole("group", { name: "Mesas livres" })).toBeNull();

    await userEvent.click(within(folha).getByRole("button", { name: "7" }));
    expect(within(folha).getByLabelText("Comanda 7")).toBeVisible();
    await userEvent.click(within(folha).getByRole("button", { name: "OK" }));

    await vi.waitFor(() => expect(chamadas.find((c) => c.path === "/comandas" && c.method === "POST")?.body).toEqual({ identificacao: "Comanda 7", mesaId: null, pessoas: null }));
  });

  it("gives the comanda a free name", async () => {
    const chamadas = stubRede({ ...base, "GET /comandas": json([]), "GET /mesas": json(mesas), "POST /comandas": json(comanda(1, { id: "novo", identificacao: "Ana" }), { status: 201 }) });
    await renderEm("/salao");
    const folha = await abrirFolha();

    await userEvent.click(within(folha).getByRole("button", { name: "Dar um nome" }));
    const nome = await screen.findByRole("dialog", { name: "Dar um nome" });
    expect(within(nome).getByRole("button", { name: "OK" })).toBeDisabled();
    await userEvent.type(within(nome).getByLabelText("Nome da comanda"), "  Ana ");
    await userEvent.click(within(nome).getByRole("button", { name: "OK" }));

    await vi.waitFor(() => expect(chamadas.find((c) => c.path === "/comandas" && c.method === "POST")?.body).toEqual({ identificacao: "Ana", mesaId: null, pessoas: null }));
  });

  it("erases digits, ignores a leading zero and stops at three digits", async () => {
    stubRede({ ...base, "GET /comandas": json([]), "GET /mesas": json([]) });
    await renderEm("/salao");
    const folha = await abrirFolha();

    await userEvent.click(within(folha).getByRole("button", { name: "0" }));
    expect(within(folha).getByLabelText("Comanda em branco")).toBeVisible();
    for (const d of ["1", "2", "3", "4"]) {
      await userEvent.click(within(folha).getByRole("button", { name: d }));
    }

    expect(within(folha).getByLabelText("Comanda 123")).toBeVisible();
    await userEvent.click(within(folha).getByRole("button", { name: "Apagar" }));
    expect(within(folha).getByLabelText("Comanda 12")).toBeVisible();
  });

  it("closes with Cancelar and keeps the sheet open with an error when the server refuses", async () => {
    stubRede({ ...base, "GET /comandas": json([]), "GET /mesas": json([]), "POST /comandas": new Response(null, { status: 403 }) });
    await renderEm("/salao");
    let folha = await abrirFolha();
    await userEvent.click(within(folha).getByRole("button", { name: "5" }));
    await userEvent.click(within(folha).getByRole("button", { name: "OK" }));
    expect(await screen.findByText("Você não tem permissão para abrir comandas.")).toBeVisible();

    await userEvent.click(within(folha).getByRole("button", { name: "Cancelar" }));
    expect(screen.queryByRole("dialog")).toBeNull();
    folha = await abrirFolha();
    expect(within(folha).getByLabelText("Comanda em branco")).toBeVisible();
  });
});

describe("list rules", () => {
  it("writes elapsed time in the words of the floor", () => {
    const agora = new Date("2026-11-06T20:00:00Z").getTime();
    expect(tempoDecorrido("2026-11-06T19:59:40Z", agora)).toBe("agora");
    expect(tempoDecorrido("2026-11-06T19:48:00Z", agora)).toBe("há 12 min");
    expect(tempoDecorrido("2026-11-06T18:48:00Z", agora)).toBe("há 1 h 12 min");
    expect(tempoDecorrido("", agora)).toBe("");
  });

  it("matches a search by number or accent-free name", () => {
    const c = paraComanda({ id: "1", numero: 12, numeroTexto: "#12", identificacao: "Mesa dos primos" });
    expect(combinaBusca(c, "12")).toBe(true);
    expect(combinaBusca(c, "#12")).toBe(true);
    expect(combinaBusca(c, "PRIMOS")).toBe(true);
    expect(combinaBusca(c, "1")).toBe(false);
    expect(combinaBusca(c, "")).toBe(true);
  });

  it("names a comanda by its table or by the name it was given", () => {
    expect(nomeExibido(paraComanda({ id: "1", numero: 1, numeroTexto: "#1", identificacao: "6", mesaId: "m6" }))).toBe("Mesa 6");
    expect(nomeExibido(paraComanda({ id: "2", numero: 2, numeroTexto: "#2", identificacao: "Ana", mesaId: null }))).toBe("Ana");
  });

  it("finds the table an entered number refers to", () => {
    expect(acharMesaPorNumero([{ id: "a", identificacao: "12", status: "livre" }], "12")?.id).toBe("a");
    expect(acharMesaPorNumero([{ id: "a", identificacao: "Varanda", status: "livre" }], "12")).toBeUndefined();
  });
});
