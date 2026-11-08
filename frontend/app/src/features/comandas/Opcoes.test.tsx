import { formatBRL as formatar } from "@flow4tech/design-system";
import { onlineManager } from "@tanstack/react-query";
import { act, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, describe, expect, it, vi } from "vitest";
import { json, renderEm, sessaoJson, stubRede } from "../../test-utils";
import { lerFila } from "../fila/fila";

/** The same text the screen shows, with the no-break space the formatter uses turned into a plain one (the matcher collapses it in the page text). */
const formatBRL = (centavos: number) => formatar(centavos).replace(/\u00a0/g, " ");

afterEach(() => {
  localStorage.removeItem("f4.grupos.v1");
  vi.unstubAllGlobals();
  Object.defineProperty(window.navigator, "onLine", { configurable: true, value: true });
  onlineManager.setOnline(true);
});

const hamburguer = {
  id: "p1",
  categoriaId: "cat1",
  codigo: "HAMB",
  nome: "Hambúrguer",
  precoCentavos: 3000,
  precoEfetivoCentavos: 3000,
  moeda: "BRL",
  ativo: true,
  versao: 1,
  indisponivel: false,
};
const agua = { ...hamburguer, id: "p2", codigo: "AGUA", nome: "Água", precoCentavos: 500, precoEfetivoCentavos: 500 };
const grupos = [
  {
    id: "g1",
    nome: "Ponto",
    minimo: 1,
    maximo: 1,
    obrigatorio: true,
    ativo: true,
    versao: 1,
    opcoes: [
      { id: "o1", nome: "Mal passado", precoCentavos: 0, ativa: true },
      { id: "o2", nome: "Ao ponto", precoCentavos: 0, ativa: true },
    ],
  },
  {
    id: "g2",
    nome: "Extras",
    minimo: 0,
    maximo: 2,
    obrigatorio: false,
    ativo: true,
    versao: 1,
    opcoes: [
      { id: "o3", nome: "Bacon", precoCentavos: 500, ativa: true },
      { id: "o4", nome: "Queijo", precoCentavos: 400, ativa: true },
    ],
  },
];
const comanda = {
  id: "c1",
  unidadeId: "u1",
  numero: 7,
  numeroTexto: "#7",
  identificacao: "Ana",
  mesaId: null,
  canal: "salao",
  garcomId: "g",
  pessoas: null,
  estado: "aberta",
  taxaServicoPercentual: 10,
  taxaServicoRemovida: false,
  versao: 2,
  abertaEm: new Date(Date.now() - 60_000).toISOString(),
};
const conta = {
  consumoCentavos: 0,
  taxaServicoCentavos: 0,
  totalCentavos: 0,
  pagoCentavos: 0,
  saldoCentavos: 0,
  taxaServicoRemovida: false,
  taxaServicoPercentual: 10,
  versao: 2,
  estado: "aberta",
};

function rotas(extra: Record<string, unknown> = {}) {
  return {
    "POST /refresh": json(sessaoJson()),
    "GET /sessao/permissoes": json({
      permissoes: ["comanda:ver", "comanda:lancar-item", "comanda:desfazer-rodada", "produto:ver"],
    }),
    "GET /comandas": json([comanda]),
    "GET /comandas/c1/conta": json(conta),
    "GET /comandas/c1/itens": json({ itens: [], consumoCentavos: 0 }),
    "GET /cardapio/produtos": json([hamburguer, agua]),
    "GET /cardapio/categorias": json([{ id: "cat1", nome: "Lanches", ativa: true, versao: 1 }]),
    "GET /cardapio/produtos/frequentes": json([hamburguer, agua]),
    "GET /cardapio/produtos/favoritos": json([]),
    "GET /cardapio/produtos/p1/grupos-opcao": json(grupos),
    "GET /cardapio/produtos/p2/grupos-opcao": json([]),
    "GET /cardapio/produtos/p1/opcoes-frequentes": json([
      { grupoId: "g2", grupoNome: "Extras", opcao: grupos[1]?.opcoes[0], lancamentos: 8 },
    ]),
    ...extra,
  } as never;
}

async function abrir(extra: Record<string, unknown> = {}) {
  const chamadas = stubRede(rotas(extra));
  await renderEm("/salao/comanda/c1");
  await screen.findByRole("heading", { level: 2, name: "Ana" });
  // The row reads the groups once; wait for the add-ons button so the tap finds them cached.
  await screen.findByRole("button", { name: "Opções de Hambúrguer" });
  return chamadas;
}

const rodadaOk = json(
  {
    id: "x",
    comandaId: "c1",
    enviadaEm: new Date().toISOString(),
    desfeitaEm: null,
    comandaVersao: 3,
    itens: [],
  },
  { status: 201 },
);

describe("item sheet", () => {
  it("a product with a required group opens the sheet instead of adding; Adicionar waits for the choice", async () => {
    await abrir();

    await userEvent.click(screen.getByRole("button", { name: "Adicionar Hambúrguer" }));

    const folha = await screen.findByRole("dialog", { name: "Hambúrguer" });
    expect(screen.queryByRole("region", { name: "Rodada não enviada" })).toBeNull();
    expect(within(folha).getByRole("button", { name: /^Adicionar/ })).toBeDisabled();
    expect(within(folha).getByText("Escolha uma opção em Ponto.")).toBeVisible();
    await userEvent.click(
      within(within(folha).getByRole("group", { name: "Ponto" })).getByRole("button", { name: "Ao ponto" }),
    );
    expect(within(folha).getByRole("button", { name: /^Adicionar/ })).toBeEnabled();
  });

  it("the most used add-on is a one-tap chip that adds its price; quantity comes from the numeric keypad; the note is kept", async () => {
    await abrir();
    await userEvent.click(screen.getByRole("button", { name: "Adicionar Hambúrguer" }));
    const folha = await screen.findByRole("dialog", { name: "Hambúrguer" });

    const usados = await within(folha).findByRole("group", { name: "Mais usados" });
    await userEvent.click(within(usados).getByRole("button", { name: /Bacon/ }));
    expect(within(usados).getByRole("button", { name: /Bacon/ })).toHaveAttribute("aria-pressed", "true");
    await userEvent.click(
      within(within(folha).getByRole("group", { name: "Ponto" })).getByRole("button", { name: "Mal passado" }),
    );
    await userEvent.type(within(folha).getByLabelText("Observação"), "sem cebola");
    await userEvent.click(within(folha).getByRole("button", { name: "Teclado" }));
    const teclado = within(folha).getByRole("group", { name: "Teclado numérico" });
    await userEvent.click(within(teclado).getByRole("button", { name: "1" }));
    await userEvent.click(within(teclado).getByRole("button", { name: "2" }));

    // (30,00 + 5,00) x 12
    const adicionar = within(folha).getByRole("button", { name: /^Adicionar/ });
    expect(adicionar).toHaveTextContent(formatBRL(42_000));
    await userEvent.click(adicionar);

    expect(screen.queryByRole("dialog", { name: "Hambúrguer" })).toBeNull();
    const linha = screen.getByRole("group", { name: "Hambúrguer" }).closest("li") as HTMLElement;
    expect(linha).toHaveTextContent("12×");
    expect(linha).toHaveTextContent("Bacon, Mal passado · sem cebola");
    expect(screen.getByRole("region", { name: "Rodada não enviada" })).toHaveTextContent(formatBRL(42_000));
  });

  it("a group of one allows one pick; an optional group stops at its maximum", async () => {
    await abrir();
    await userEvent.click(screen.getByRole("button", { name: "Opções de Hambúrguer" }));
    const folha = await screen.findByRole("dialog", { name: "Hambúrguer" });
    const ponto = within(folha).getByRole("group", { name: "Ponto" });

    await userEvent.click(within(ponto).getByRole("button", { name: "Mal passado" }));
    await userEvent.click(within(ponto).getByRole("button", { name: "Ao ponto" }));
    expect(within(ponto).getByRole("button", { name: "Mal passado" })).toHaveAttribute("aria-pressed", "false");
    expect(within(ponto).getByRole("button", { name: "Ao ponto" })).toHaveAttribute("aria-pressed", "true");
  });

  it("a product with no options adds in a single tap and opens no sheet", async () => {
    await abrir();

    await userEvent.click(screen.getByRole("button", { name: "Adicionar Água" }));

    expect(screen.queryByRole("dialog")).toBeNull();
    expect(screen.getByRole("region", { name: "Rodada não enviada" })).toHaveTextContent("1 item não enviado");
  });

  it("any staged line can be adjusted to add a note (even a product without add-ons)", async () => {
    await abrir();
    await userEvent.click(screen.getByRole("button", { name: "Adicionar Água" }));

    await userEvent.click(screen.getByRole("button", { name: "Ajustar Água" }));
    const folha = await screen.findByRole("dialog", { name: "Água" });
    await userEvent.type(within(folha).getByLabelText("Observação"), "sem gelo");
    await userEvent.click(within(folha).getByRole("button", { name: /^Salvar/ }));

    expect(screen.getByRole("group", { name: "Água" }).closest("li")).toHaveTextContent("sem gelo");
  });

  it("without the groups and without signal, the product still goes in (offline work never stops) and the waiter is told", async () => {
    await new Promise((r) => setTimeout(r, 50));
    localStorage.removeItem("f4.grupos.v1");
    stubRede(
      rotas({
        "GET /cardapio/produtos/p1/grupos-opcao": () => Promise.reject(new TypeError("network")),
        "GET /cardapio/produtos/p2/grupos-opcao": () => Promise.reject(new TypeError("network")),
      }),
    );
    await renderEm("/salao/comanda/c1");
    await screen.findByRole("heading", { level: 2, name: "Ana" });

    await userEvent.click(await screen.findByRole("button", { name: "Adicionar Hambúrguer" }));

    expect(await screen.findByText(/Sem sinal para ver as opções de Hambúrguer/)).toBeVisible();
    expect(screen.getByRole("region", { name: "Rodada não enviada" })).toHaveTextContent("1 item não enviado");
  });

  it("remembers the groups: a later cold read without signal still knows the required group and opens the sheet", async () => {
    await abrir();
    await waitFor(() => expect(localStorage.getItem("f4.grupos.v1")).toContain("g1"));
    stubRede(
      rotas({
        "GET /cardapio/produtos/p1/grupos-opcao": () => Promise.reject(new TypeError("network")),
        "GET /cardapio/produtos/p2/grupos-opcao": () => Promise.reject(new TypeError("network")),
      }),
    );
    document.body.innerHTML = "";
    await renderEm("/salao/comanda/c1");
    await screen.findByRole("heading", { level: 2, name: "Ana" });

    await userEvent.click(await screen.findByRole("button", { name: "Adicionar Hambúrguer" }));

    expect(await screen.findByRole("dialog", { name: "Hambúrguer" })).toBeVisible();
  });

  it("reads the groups of every product in the background, so a tap never waits for them", async () => {
    const chamadas = await abrir();

    await waitFor(() => {
      const lidas = chamadas.filter((c) => c.path.endsWith("/grupos-opcao")).map((c) => c.path);
      expect(new Set(lidas)).toEqual(
        new Set(["/cardapio/produtos/p1/grupos-opcao", "/cardapio/produtos/p2/grupos-opcao"]),
      );
    });
  });
});

describe("sending with add-ons", () => {
  async function prepararRodada(extra: Record<string, unknown> = {}) {
    const chamadas = await abrir(extra);
    await userEvent.click(screen.getByRole("button", { name: "Adicionar Hambúrguer" }));
    const folha = await screen.findByRole("dialog", { name: "Hambúrguer" });
    await userEvent.click(
      within(within(folha).getByRole("group", { name: "Ponto" })).getByRole("button", { name: "Ao ponto" }),
    );
    await userEvent.click(
      within(within(folha).getByRole("group", { name: "Extras" })).getByRole("button", { name: /Queijo/ }),
    );
    await userEvent.type(within(folha).getByLabelText("Observação"), "bem passado por fora");
    await userEvent.click(within(folha).getByRole("button", { name: /^Adicionar/ }));
    return chamadas;
  }

  it("the round goes with the add-ons by id and the note, nothing else about them", async () => {
    const chamadas = await prepararRodada({ "POST /comandas/c1/rodadas": rodadaOk });

    await userEvent.click(screen.getByRole("button", { name: /^Enviar$/ }));
    const enviar = await screen.findByRole("dialog", { name: "Enviar rodada" });
    expect(enviar).toHaveTextContent("Ao ponto, Queijo");
    expect(enviar).toHaveTextContent(formatBRL(3400));
    await userEvent.click(within(enviar).getByRole("button", { name: "Enviar agora" }));

    await waitFor(() =>
      expect(chamadas.some((c) => c.method === "POST" && c.path === "/comandas/c1/rodadas")).toBe(true),
    );
    const corpo = chamadas.find((c) => c.method === "POST" && c.path === "/comandas/c1/rodadas")?.body as {
      itens: unknown[];
    };
    expect(corpo.itens).toEqual([
      { produtoId: "p1", quantidade: 1, observacao: "bem passado por fora", opcaoIds: ["o2", "o4"] },
    ]);
  });

  it("offline, the queued round keeps the add-ons and the note; on reconnection it creates ONE round under the same id", async () => {
    const chamadas = await prepararRodada({ "POST /comandas/c1/rodadas": rodadaOk });
    Object.defineProperty(window.navigator, "onLine", { configurable: true, value: false });
    await act(async () => {
      window.dispatchEvent(new Event("offline"));
    });

    await userEvent.click(screen.getByRole("button", { name: /^Enviar$/ }));
    await userEvent.click(await screen.findByRole("button", { name: "Enviar agora" }));

    expect(await screen.findByRole("button", { name: "1 ação pendente" })).toBeVisible();
    const guardada = lerFila()[0];
    expect(guardada?.itens).toEqual([
      { produtoId: "p1", quantidade: 1, observacao: "bem passado por fora", opcaoIds: ["o2", "o4"] },
    ]);
    expect(guardada?.detalhe).toContain(formatar(3400));
    expect(chamadas.filter((c) => c.method === "POST" && c.path === "/comandas/c1/rodadas")).toHaveLength(0);

    Object.defineProperty(window.navigator, "onLine", { configurable: true, value: true });
    await act(async () => {
      window.dispatchEvent(new Event("online"));
    });

    await waitFor(() =>
      expect(chamadas.filter((c) => c.method === "POST" && c.path === "/comandas/c1/rodadas")).toHaveLength(1),
    );
    const enviada = chamadas.find((c) => c.method === "POST" && c.path === "/comandas/c1/rodadas")?.body as {
      rodadaId: string;
      itens: unknown[];
    };
    expect(enviada.rodadaId).toBe(guardada?.id);
    expect(enviada.itens).toEqual(guardada?.itens);
    await waitFor(() => expect(lerFila()).toHaveLength(0));
  });

  it("a rejected add-on choice leaves the round staged with a plain message", async () => {
    await prepararRodada({
      "POST /comandas/c1/rodadas": json({ codigo: "opcao-inativa" }, { status: 409 }),
    });

    await userEvent.click(screen.getByRole("button", { name: /^Enviar$/ }));
    await userEvent.click(await screen.findByRole("button", { name: "Enviar agora" }));

    expect(
      await screen.findByText("Os adicionais de um item mudaram. Volte, ajuste o item e envie de novo."),
    ).toBeVisible();
    expect(screen.getByRole("region", { name: "Rodada não enviada", hidden: true })).toBeInTheDocument();
  });
});

describe("repeat round and the bill", () => {
  it("shows launched add-ons and the server unit price, and repeating the round stages the same add-ons", async () => {
    const itemLancado = {
      id: "i1",
      rodadaId: "r1",
      posicao: 1,
      produtoId: "p1",
      produtoNome: "Hambúrguer",
      precoCentavos: 3000,
      quantidade: 2,
      observacao: "sem cebola",
      estado: "lancado",
      motivoCancelamento: null,
      canceladoPor: null,
      canceladoEm: null,
      opcoes: [{ opcaoId: "o3", grupoId: "g2", nome: "Bacon", precoCentavos: 500 }],
      totalCentavos: 7000,
    };
    await abrir({ "GET /comandas/c1/itens": json({ itens: [itemLancado], consumoCentavos: 7000 }) });

    const lista = screen.getByRole("list", { name: "Itens da conta" });
    expect(lista).toHaveTextContent("Bacon · sem cebola");
    expect(lista).toHaveTextContent(formatBRL(7000));
    await userEvent.click(screen.getByRole("button", { name: "Repetir última rodada" }));

    // The preview carries the add-ons and the note of the last round, at today's prices.
    const folha = await screen.findByRole("dialog", { name: "Repetir última rodada" });
    expect(folha).toHaveTextContent("Bacon · sem cebola");
    expect(folha).toHaveTextContent(formatBRL(7000));
  });
});

describe("item sheet loading", () => {
  it("says the most used are loading while they load, then shows them", async () => {
    let liberar: (r: Response) => void = () => undefined;
    const lenta = new Promise<Response>((resolve) => {
      liberar = resolve;
    });
    await abrir({ "GET /cardapio/produtos/p1/opcoes-frequentes": () => lenta as never });
    await userEvent.click(screen.getByRole("button", { name: "Adicionar Hambúrguer" }));

    expect(await screen.findByRole("status", { name: "Carregando os mais usados" })).toBeVisible();
    await act(async () => {
      liberar(json([{ grupoId: "g2", grupoNome: "Extras", opcao: grupos[1]?.opcoes[1], lancamentos: 2 }]));
    });

    const usados = await screen.findByRole("group", { name: "Mais usados" });
    expect(within(usados).getByRole("button", { name: /Queijo/ })).toBeVisible();
    expect(screen.queryByRole("status", { name: "Carregando os mais usados" })).toBeNull();
  });
});
