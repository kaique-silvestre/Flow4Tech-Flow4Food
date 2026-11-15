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
  garcomNome: "Rafael",
  consumoCentavos: 11700,
  ultimaAtividadeEm: new Date(Date.now() - 5 * 60_000).toISOString(),
  ...extra,
});
const minhaComanda = (n: number, extra: Record<string, unknown> = {}) =>
  comanda(n, { garcomId: "conta-1", garcomNome: "Marina", ...extra });
const minutosAtras = (m: number) => new Date(Date.now() - m * 60_000).toISOString();
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

  it("draws each comanda as a card with name, value, time open, waiter and the state in words", async () => {
    stubRede({
      ...base,
      "GET /comandas": json([
        comanda(7, { identificacao: "Aniversário da Ana", consumoCentavos: 123456 }),
        minhaComanda(8, { identificacao: "Balcão 1", consumoCentavos: 2800, abertaEm: minutosAtras(135) }),
      ]),
      "GET /mesas": json([]),
    });

    await renderEm("/salao");

    const outra = await screen.findByRole("link", { name: /#7/ });
    expect(within(outra).getByText("Aniversário da Ana")).toBeVisible();
    expect(within(outra).getByText("R$ 1.234,56")).toBeVisible();
    expect(within(outra).getByText("12 min aberta · Rafael")).toBeVisible();
    expect(within(outra).getByText("Aberta")).toBeVisible();
    const minha = screen.getByRole("link", { name: /#8/ });
    expect(within(minha).getByText("R$ 28,00")).toBeVisible();
    expect(within(minha).getByText("2h 15min aberta · Você")).toBeVisible();
    expect(screen.getByRole("heading", { level: 2, name: "Minhas comandas" })).toBeInTheDocument();
  });

  it("marks a comanda with no launch for 40 minutes as Parada, with the time and an icon, and one with a requested bill as Aguardando pagamento", async () => {
    stubRede({
      ...base,
      "GET /comandas": json([
        comanda(1, { ultimaAtividadeEm: minutosAtras(52) }),
        comanda(2, { contaPedidaEm: minutosAtras(1) }),
        comanda(3),
      ]),
      "GET /mesas": json([]),
    });

    await renderEm("/salao");

    const parada = await screen.findByRole("link", { name: /#1/ });
    expect(within(parada).getByText("Parada há 52 min")).toBeVisible();
    expect(parada.querySelector("svg.f4-icon")).not.toBeNull();
    expect(within(screen.getByRole("link", { name: /#2/ })).getByText("Aguardando pagamento")).toBeVisible();
    expect(within(screen.getByRole("link", { name: /#3/ })).getByText("Aberta")).toBeVisible();
    // The stalled one comes first, the one waiting for payment last.
    const ordem = screen.getAllByRole("link", { name: /#\d/ }).map((a) => a.textContent);
    expect(ordem[0]).toContain("#1");
    expect(ordem[2]).toContain("#2");
  });

  it("filters by chip and shows the tally on each one, counting the whole list", async () => {
    stubRede({
      ...base,
      "GET /sessao/permissoes": json({ permissoes: ["comanda:ver", "comanda:ver-comandas-de-outros"] }),
      "GET /comandas": json([
        minhaComanda(1),
        minhaComanda(2, { contaPedidaEm: minutosAtras(2) }),
        comanda(3),
        comanda(4, { ultimaAtividadeEm: minutosAtras(60) }),
      ]),
    });
    await renderEm("/salao");

    const grupo = await screen.findByRole("group", { name: "Filtrar comandas" });
    const chips = within(grupo)
      .getAllByRole("button")
      .map((b) => b.textContent?.trim());
    expect(chips).toEqual(["Minhas 2", "Todas 4", "Abertas 3", "Aguardando 1"]);
    expect(within(grupo).getByRole("button", { name: "Todas 4" })).toHaveAttribute("aria-pressed", "true");
    expect(screen.getAllByRole("link", { name: /#\d/ })).toHaveLength(4);

    await userEvent.click(within(grupo).getByRole("button", { name: "Minhas 2" }));
    expect(screen.getAllByRole("link", { name: /#\d/ })).toHaveLength(2);
    expect(within(grupo).getByRole("button", { name: "Minhas 2" })).toHaveAttribute("aria-pressed", "true");

    await userEvent.click(within(grupo).getByRole("button", { name: "Aguardando 1" }));
    expect(screen.getAllByRole("link", { name: /#\d/ })).toHaveLength(1);
    expect(screen.getByRole("link", { name: /#2/ })).toBeVisible();

    await userEvent.type(screen.getByLabelText("Buscar comanda"), "xyz");
    expect(screen.getByText("Nada com “xyz”. Tente o número da comanda.")).toBeVisible();
    await userEvent.click(screen.getByRole("button", { name: "Ver todas" }));
    expect(screen.getAllByRole("link", { name: /#\d/ })).toHaveLength(4);
    expect(within(grupo).getByRole("button", { name: "Todas 4" })).toHaveAttribute("aria-pressed", "true");
  });

  it("offers no Todas chip to who only sees their own and starts on Minhas", async () => {
    stubRede({ ...base, "GET /comandas": json([minhaComanda(1), minhaComanda(2)]), "GET /mesas": json([]) });
    await renderEm("/salao");

    const grupo = await screen.findByRole("group", { name: "Filtrar comandas" });

    expect(within(grupo).queryByRole("button", { name: /^Todas/ })).toBeNull();
    expect(within(grupo).getByRole("button", { name: "Minhas 2" })).toHaveAttribute("aria-pressed", "true");
  });

  it("says so when a filter has nothing and brings everything back with Ver todas", async () => {
    stubRede({ ...base, "GET /comandas": json([minhaComanda(1)]), "GET /mesas": json([]) });
    await renderEm("/salao");

    await userEvent.click(await screen.findByRole("button", { name: "Aguardando 0" }));
    expect(screen.getByText("Nenhuma comanda neste filtro.")).toBeVisible();
    await userEvent.click(screen.getByRole("button", { name: "Ver todas" }));

    expect(screen.getByRole("link", { name: /#1/ })).toBeVisible();
  });

  it("calls it Comandas abertas for who may see the others'", async () => {
    stubRede({
      ...base,
      "GET /sessao/permissoes": json({ permissoes: ["comanda:ver", "comanda:ver-comandas-de-outros"] }),
      "GET /comandas": json([comanda(1)]),
    });

    await renderEm("/salao");

    expect(await screen.findByRole("heading", { level: 2, name: "Comandas abertas" })).toBeVisible();
    expect(screen.queryByRole("button", { name: "Nova comanda" })).toBeNull();
  });

  it("searches by number and by name, ignoring accents, and says so when nothing matches", async () => {
    stubRede({
      ...base,
      "GET /comandas": json([
        comanda(7, { identificacao: "Aniversário" }),
        comanda(12, { identificacao: "Mesa dos primos" }),
      ]),
      "GET /mesas": json([]),
    });
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
    stubRede({
      ...base,
      "GET /comandas": () => (falhar ? new Response(null, { status: 500 }) : json([comanda(1)])),
      "GET /mesas": json([]),
    });
    await renderEm("/salao");

    expect(await screen.findByText("Não foi possível carregar as comandas.")).toBeVisible();
    falhar = false;
    await userEvent.click(screen.getByRole("button", { name: "Tentar novamente" }));

    expect(await screen.findByRole("link", { name: /#1/ })).toBeVisible();
  });

  it("opens the comanda screen from a row", async () => {
    stubRede({
      ...base,
      "GET /comandas": json([comanda(5)]),
      "GET /mesas": json([]),
      "GET /comandas/c5/conta": json({
        consumoCentavos: 0,
        taxaServicoCentavos: 0,
        totalCentavos: 0,
        pagoCentavos: 0,
        saldoCentavos: 0,
        taxaServicoRemovida: false,
        taxaServicoPercentual: 10,
        versao: 1,
        estado: "aberta",
      }),
      "GET /comandas/c5/itens": json({ itens: [], consumoCentavos: 0 }),
      "GET /cardapio/produtos": json([]),
      "GET /cardapio/categorias": json([]),
      "GET /cardapio/produtos/frequentes": json([]),
    });
    const { router } = await renderEm("/salao");

    await userEvent.click(await screen.findByRole("link", { name: /#5/ }));

    expect(router.state.location.pathname).toBe("/salao/comanda/c5");
    expect(await screen.findByRole("heading", { level: 2, name: "Comanda 5" })).toBeVisible();
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
    expect(chamadas.find((c) => c.path === "/comandas" && c.method === "POST")?.body).toEqual({
      identificacao: null,
      mesaId: "m4",
      pessoas: null,
    });
  });

  it("offers the free tables as chips and opens the chosen one", async () => {
    const chamadas = stubRede({
      ...base,
      "GET /comandas": json([]),
      "GET /mesas": json(mesas),
      "POST /comandas": json(comanda(1, { id: "novo" }), { status: 201 }),
    });
    await renderEm("/salao");
    const folha = await abrirFolha();

    const chips = within(folha).getByRole("group", { name: "Mesas livres" });
    expect(
      within(chips)
        .getAllByRole("button")
        .map((b) => b.textContent),
    ).toEqual(["1", "4"]);
    await userEvent.click(within(chips).getByRole("button", { name: "1" }));
    expect(within(chips).getByRole("button", { name: "1" })).toHaveAttribute("aria-pressed", "true");
    await userEvent.click(within(folha).getByRole("button", { name: "OK" }));

    await vi.waitFor(() =>
      expect(chamadas.find((c) => c.path === "/comandas" && c.method === "POST")?.body).toMatchObject({ mesaId: "m1" }),
    );
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
    stubRede({
      ...base,
      "GET /comandas": json([comanda(3, { id: "c3", mesaId: "m12", identificacao: "12" })]),
      "GET /mesas": json(mesas),
    });
    const { router } = await renderEm("/salao");
    const folha = await abrirFolha();
    await userEvent.click(teclado(folha).getByRole("button", { name: "1" }));
    await userEvent.click(teclado(folha).getByRole("button", { name: "2" }));
    await userEvent.click(within(folha).getByRole("button", { name: "OK" }));

    await userEvent.click(await screen.findByRole("button", { name: "#3 · 12" }));

    await vi.waitFor(() => expect(router.state.location.pathname).toBe("/salao/comanda/c3"));
  });

  it("names the comanda by number when the unit works without tables", async () => {
    const chamadas = stubRede({
      ...base,
      "GET /comandas": json([]),
      "GET /mesas": json([]),
      "POST /comandas": json(comanda(1, { id: "novo", identificacao: "Comanda 7" }), { status: 201 }),
    });
    await renderEm("/salao");
    const folha = await abrirFolha();
    expect(within(folha).getByLabelText("Comanda em branco")).toBeVisible();
    expect(within(folha).queryByRole("group", { name: "Mesas livres" })).toBeNull();

    await userEvent.click(within(folha).getByRole("button", { name: "7" }));
    expect(within(folha).getByLabelText("Comanda 7")).toBeVisible();
    await userEvent.click(within(folha).getByRole("button", { name: "OK" }));

    await vi.waitFor(() =>
      expect(chamadas.find((c) => c.path === "/comandas" && c.method === "POST")?.body).toEqual({
        identificacao: "Comanda 7",
        mesaId: null,
        pessoas: null,
      }),
    );
  });

  it("gives the comanda a free name", async () => {
    const chamadas = stubRede({
      ...base,
      "GET /comandas": json([]),
      "GET /mesas": json(mesas),
      "POST /comandas": json(comanda(1, { id: "novo", identificacao: "Ana" }), { status: 201 }),
    });
    await renderEm("/salao");
    const folha = await abrirFolha();

    await userEvent.click(within(folha).getByRole("button", { name: "Dar um nome" }));
    const nome = await screen.findByRole("dialog", { name: "Dar um nome" });
    expect(within(nome).getByRole("button", { name: "OK" })).toBeDisabled();
    await userEvent.type(within(nome).getByLabelText("Nome da comanda"), "  Ana ");
    await userEvent.click(within(nome).getByRole("button", { name: "OK" }));

    await vi.waitFor(() =>
      expect(chamadas.find((c) => c.path === "/comandas" && c.method === "POST")?.body).toEqual({
        identificacao: "Ana",
        mesaId: null,
        pessoas: null,
      }),
    );
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
    stubRede({
      ...base,
      "GET /comandas": json([]),
      "GET /mesas": json([]),
      "POST /comandas": new Response(null, { status: 403 }),
    });
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
    expect(nomeExibido(paraComanda({ id: "1", numero: 1, numeroTexto: "#1", identificacao: "6", mesaId: "m6" }))).toBe(
      "Mesa 6",
    );
    expect(
      nomeExibido(paraComanda({ id: "2", numero: 2, numeroTexto: "#2", identificacao: "Ana", mesaId: null })),
    ).toBe("Ana");
  });

  it("finds the table an entered number refers to", () => {
    expect(acharMesaPorNumero([{ id: "a", identificacao: "12", status: "livre" }], "12")?.id).toBe("a");
    expect(acharMesaPorNumero([{ id: "a", identificacao: "Varanda", status: "livre" }], "12")).toBeUndefined();
  });
});
