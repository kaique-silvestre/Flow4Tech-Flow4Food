import { screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, describe, expect, it, vi } from "vitest";
import { json, renderEm, sessaoJson, stubRede } from "../../test-utils";

afterEach(() => {
  vi.unstubAllGlobals();
});

const minutosAtras = (n: number) => new Date(Date.now() - n * 60_000).toISOString();
const mesa = (id: string, identificacao: string, extra: Record<string, unknown> = {}) => ({
  id,
  identificacao,
  capacidade: null,
  areaId: null,
  status: "livre",
  comandasAbertas: 0,
  abertaDesde: null,
  ...extra,
});
const mapa = {
  areas: [
    {
      id: "a1",
      nome: "Salão",
      mesas: [
        mesa("m1", "1", { areaId: "a1", capacidade: 4 }),
        mesa("m2", "2", { areaId: "a1", status: "ocupada", comandasAbertas: 1, abertaDesde: minutosAtras(12) }),
      ],
    },
    {
      id: "a2",
      nome: "Varanda",
      mesas: [
        mesa("m3", "V1", {
          areaId: "a2",
          status: "aguardando-conta",
          comandasAbertas: 2,
          abertaDesde: minutosAtras(70),
        }),
      ],
    },
  ],
  semArea: [mesa("m9", "Balcão")],
};
const comanda = (id: string, numero: number, mesaId: string | null, extra: Record<string, unknown> = {}) => ({
  id,
  unidadeId: "u1",
  numero,
  numeroTexto: `#${numero}`,
  identificacao: mesaId ? "2" : `Comanda ${numero}`,
  mesaId,
  canal: "salao",
  garcomId: "g1",
  pessoas: null,
  estado: "aberta",
  taxaServicoPercentual: 10,
  taxaServicoRemovida: false,
  versao: 3,
  abertaEm: minutosAtras(12),
  ...extra,
});
const base = { "POST /refresh": json(sessaoJson()) };
const permissoes = (...p: string[]) => ({ "GET /sessao/permissoes": json({ permissoes: p }) });
const tudo = ["mesa:ver", "comanda:ver", "comanda:abrir", "comanda:transferir-mesa"];

describe("table map screen", () => {
  it("lists the tables by area with the status in words and the elapsed time", async () => {
    stubRede({ ...base, ...permissoes(...tudo), "GET /mesas/mapa": json(mapa), "GET /comandas": json([]) });

    await renderEm("/salao/mesas");

    expect(await screen.findByRole("heading", { level: 3, name: /Salão/ })).toBeVisible();
    expect(screen.getByRole("heading", { level: 3, name: /Varanda/ })).toBeVisible();
    expect(screen.getByRole("heading", { level: 3, name: /Outras mesas/ })).toBeVisible();
    const livre = screen.getByRole("button", { name: /Mesa 1, livre/ });
    expect(within(livre).getByText("Livre")).toBeVisible();
    expect(within(livre).getByText("4 lugares")).toBeVisible();
    const ocupada = screen.getByRole("button", { name: /Mesa 2, ocupada/ });
    expect(within(ocupada).getByText("Ocupada")).toBeVisible();
    expect(within(ocupada).getByText("há 12 min")).toBeVisible();
    const conta = screen.getByRole("button", { name: /V1, aguardando conta/ });
    expect(within(conta).getByText("Aguardando conta")).toBeVisible();
    expect(within(conta).getByText("2 comandas · há 1 h 10 min")).toBeVisible();
    expect(screen.getByRole("button", { name: /Balcão, livre/ })).toBeVisible();
  });

  it("opens a comanda on a free table with one tap and goes to it", async () => {
    const chamadas = stubRede({
      ...base,
      ...permissoes(...tudo),
      "GET /mesas/mapa": json(mapa),
      "GET /comandas": json([]),
      "POST /comandas": json(comanda("novo", 5, "m1"), { status: 201 }),
    });
    const { router } = await renderEm("/salao/mesas");

    await userEvent.click(await screen.findByRole("button", { name: /Mesa 1, livre/ }));

    await waitFor(() => expect(router.state.location.pathname).toBe("/salao/comanda/novo"));
    expect(chamadas.find((c) => c.method === "POST" && c.path === "/comandas")?.body).toEqual({
      identificacao: null,
      mesaId: "m1",
      pessoas: null,
    });
  });

  it("makes a free table static, not a disabled button, for who may not open comandas", async () => {
    stubRede({
      ...base,
      ...permissoes("mesa:ver", "comanda:ver"),
      "GET /mesas/mapa": json(mapa),
      "GET /comandas": json([]),
    });

    await renderEm("/salao/mesas");

    expect((await screen.findAllByText("Livre")).length).toBeGreaterThan(0);
    expect(screen.queryByRole("button", { name: /Mesa 1, livre/ })).toBeNull();
    expect(screen.queryByRole("button", { name: /Balcão/ })).toBeNull();
    expect(screen.getByRole("button", { name: /Mesa 2, ocupada/ })).toBeVisible();
  });

  it("opens the sheet of an occupied table with its comandas and the move action", async () => {
    stubRede({
      ...base,
      ...permissoes(...tudo),
      "GET /mesas/mapa": json(mapa),
      "GET /comandas": json([comanda("c1", 7, "m2"), comanda("c2", 8, "m9")]),
    });
    await renderEm("/salao/mesas");

    await userEvent.click(await screen.findByRole("button", { name: /Mesa 2, ocupada/ }));

    const folha = await screen.findByRole("dialog", { name: "Mesa 2" });
    expect(within(folha).getByText("1 comanda aberta nesta mesa.")).toBeVisible();
    expect(within(folha).getByRole("button", { name: "#7 · 2" })).toBeVisible();
    expect(within(folha).queryByText(/#8/)).toBeNull();
    expect(within(folha).getByRole("button", { name: "Trocar de mesa a comanda #7" })).toBeVisible();
    expect(within(folha).getByRole("button", { name: "Abrir outra comanda" })).toBeVisible();
  });

  it("does not offer the move to who lacks comanda:transferir-mesa", async () => {
    stubRede({
      ...base,
      ...permissoes("mesa:ver", "comanda:ver", "comanda:abrir"),
      "GET /mesas/mapa": json(mapa),
      "GET /comandas": json([comanda("c1", 7, "m2")]),
    });
    await renderEm("/salao/mesas");

    await userEvent.click(await screen.findByRole("button", { name: /Mesa 2, ocupada/ }));

    const folha = await screen.findByRole("dialog", { name: "Mesa 2" });
    expect(within(folha).getByRole("button", { name: "#7 · 2" })).toBeVisible();
    expect(within(folha).queryByRole("button", { name: /Trocar de mesa/ })).toBeNull();
  });

  it("says the comandas of an occupied table belong to someone else when the caller sees none", async () => {
    stubRede({ ...base, ...permissoes(...tudo), "GET /mesas/mapa": json(mapa), "GET /comandas": json([]) });
    await renderEm("/salao/mesas");

    await userEvent.click(await screen.findByRole("button", { name: /Mesa 2, ocupada/ }));

    expect(await screen.findByText("As comandas desta mesa são de outra pessoa.")).toBeVisible();
  });

  it("moves a comanda to the chosen table sending the version it saw", async () => {
    const chamadas = stubRede({
      ...base,
      ...permissoes(...tudo),
      "GET /mesas/mapa": json(mapa),
      "GET /comandas": json([comanda("c1", 7, "m2")]),
      "POST /comandas/c1/trocar-mesa": json(comanda("c1", 7, "m9", { versao: 4 })),
    });
    await renderEm("/salao/mesas");
    await userEvent.click(await screen.findByRole("button", { name: /Mesa 2, ocupada/ }));
    await userEvent.click(await screen.findByRole("button", { name: "Trocar de mesa a comanda #7" }));

    const folha = await screen.findByRole("dialog", { name: "Trocar de mesa" });
    expect(within(folha).getByText("#7 está em Mesa 2. Escolha a nova mesa.")).toBeVisible();
    expect(within(folha).queryByRole("button", { name: /^Mesa 2/ })).toBeNull();
    expect(within(folha).getByRole("button", { name: "Mover" })).toBeDisabled();
    await userEvent.click(within(folha).getByRole("button", { name: /Balcão/ }));
    await userEvent.click(within(folha).getByRole("button", { name: "Mover para Balcão" }));

    expect(await screen.findByText("#7 mudou para Balcão.")).toBeVisible();
    expect(chamadas.find((c) => c.path === "/comandas/c1/trocar-mesa")?.body).toEqual({ mesaId: "m9", versao: 3 });
    await waitFor(() => expect(screen.queryByRole("dialog")).toBeNull());
  });

  it("keeps the chosen table and refreshes the data when the comanda changed on another device", async () => {
    let versao = 3;
    const chamadas = stubRede({
      ...base,
      ...permissoes(...tudo),
      "GET /mesas/mapa": json(mapa),
      "GET /comandas": () => json([comanda("c1", 7, "m2", { versao })]),
      "POST /comandas/c1/trocar-mesa": (chamada) => {
        const enviada = (chamada.body as { versao: number }).versao;
        if (enviada !== 4) {
          versao = 4;
          return json({ codigo: "versao-desatualizada", versaoAtual: 4 }, { status: 409 });
        }

        return json(comanda("c1", 7, "m9", { versao: 5 }));
      },
    });
    await renderEm("/salao/mesas");
    await userEvent.click(await screen.findByRole("button", { name: /Mesa 2, ocupada/ }));
    await userEvent.click(await screen.findByRole("button", { name: "Trocar de mesa a comanda #7" }));
    const folha = await screen.findByRole("dialog", { name: "Trocar de mesa" });
    await userEvent.click(within(folha).getByRole("button", { name: /Balcão/ }));

    await userEvent.click(within(folha).getByRole("button", { name: "Mover para Balcão" }));

    expect(await within(folha).findByRole("alert")).toHaveTextContent("alterada em outro aparelho");
    expect(within(folha).getByRole("button", { name: "Mover para Balcão" })).toBeEnabled();
    await userEvent.click(within(folha).getByRole("button", { name: "Mover para Balcão" }));
    expect(await screen.findByText("#7 mudou para Balcão.")).toBeVisible();
    expect(
      chamadas.filter((c) => c.path === "/comandas/c1/trocar-mesa").map((c) => (c.body as { versao: number }).versao),
    ).toEqual([3, 4]);
  });

  it("shows loading, empty and error states, and tries again", async () => {
    let falhar = true;
    stubRede({
      ...base,
      ...permissoes(...tudo),
      "GET /mesas/mapa": () => (falhar ? json({}, { status: 500 }) : json({ areas: [], semArea: [] })),
      "GET /comandas": json([]),
    });
    await renderEm("/salao/mesas");

    expect(await screen.findByText("Não foi possível carregar as mesas.")).toBeVisible();
    falhar = false;
    await userEvent.click(screen.getByRole("button", { name: "Tentar novamente" }));

    expect(await screen.findByText(/Nenhuma mesa para mostrar/)).toBeVisible();
  });

  it("explains the missing permission", async () => {
    stubRede({ ...base, ...permissoes("comanda:ver") });

    await renderEm("/salao/mesas");

    expect(await screen.findByText("Você não tem permissão para ver as mesas.")).toBeVisible();
  });
});

describe("Mesas tab", () => {
  it("is in the tab bar when the unit has tables to show", async () => {
    stubRede({ ...base, ...permissoes(...tudo), "GET /mesas/mapa": json(mapa), "GET /comandas": json([]) });

    await renderEm("/salao");

    expect(await screen.findByRole("link", { name: "Mesas" })).toBeVisible();
  });

  it("is absent when the unit does not use tables (the map comes back empty)", async () => {
    stubRede({
      ...base,
      ...permissoes(...tudo),
      "GET /mesas/mapa": json({ areas: [], semArea: [] }),
      "GET /comandas": json([]),
      "GET /mesas": json([]),
    });

    await renderEm("/salao");

    await screen.findByRole("link", { name: "Comandas" });
    await waitFor(() => expect(screen.queryByRole("link", { name: "Mesas" })).toBeNull());
  });

  it("is absent without mesa:ver, and the map is not even requested", async () => {
    const chamadas = stubRede({ ...base, ...permissoes("comanda:ver"), "GET /comandas": json([]) });

    await renderEm("/salao");

    await screen.findByRole("link", { name: "Comandas" });
    expect(screen.queryByRole("link", { name: "Mesas" })).toBeNull();
    expect(chamadas.some((c) => c.path === "/mesas/mapa")).toBe(false);
  });
});
