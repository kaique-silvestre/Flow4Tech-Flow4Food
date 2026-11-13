import { onlineManager } from "@tanstack/react-query";
import { screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, describe, expect, it, vi } from "vitest";
import { json, jsonDoContrato, renderEm, sessaoJson, stubRede } from "../../test-utils";
import { lerFila } from "../fila/fila";

afterEach(() => {
  vi.unstubAllGlobals();
  Object.defineProperty(window.navigator, "onLine", { configurable: true, value: true });
  onlineManager.setOnline(true);
});

const TODAS = [
  "comanda:ver",
  "comanda:transferir-item",
  "comanda:juntar",
  "comanda:dividir",
  "mesa:ver",
  "produto:ver",
];
const base = {
  unidadeId: "u1",
  canal: "salao",
  garcomId: "g1",
  pessoas: 2,
  estado: "aberta",
  taxaServicoPercentual: 10,
  taxaServicoRemovida: false,
  abertaEm: new Date(Date.now() - 5 * 60_000).toISOString(),
  observacao: null,
  mesaId: null,
};
const c1 = { ...base, id: "c1", numero: 7, numeroTexto: "#7", identificacao: "Ana", versao: 2 };
const c2 = { ...base, id: "c2", numero: 8, numeroTexto: "#8", identificacao: "Beto", versao: 5 };
const umItem = (id: string, nome: string, preco: number, quantidade = 1) => ({
  id,
  rodadaId: "r1",
  posicao: 1,
  produtoId: `p-${id}`,
  produtoNome: nome,
  precoCentavos: preco,
  quantidade,
  observacao: null,
  estado: "lancado",
  motivoCancelamento: null,
  canceladoPor: null,
  canceladoEm: null,
  opcoes: [],
  consumoCentavos: preco * quantidade,
  tipo: "normal",
  motivoDoTipo: null,
  valorDeTabelaCentavos: preco * quantidade,
});
const ITENS = [umItem("i1", "Chopp 300ml", 1250, 2), umItem("i2", "Porção de fritas", 3000)];
const conta = {
  consumoCentavos: 5500,
  taxaServicoCentavos: 550,
  totalCentavos: 6050,
  pagoCentavos: 0,
  saldoCentavos: 6050,
  taxaServicoRemovida: false,
  taxaServicoPercentual: 10,
  versao: 2,
  estado: "aberta",
  descontoCentavos: 0,
};

type Rotas = Record<string, Response | ((c: never) => Response | Promise<Response>)>;

async function abrir(permissoes: string[] = TODAS, extra: Rotas = {}) {
  const chamadas = stubRede({
    "POST /refresh": json(sessaoJson()),
    "GET /sessao/permissoes": json({ permissoes }),
    "GET /sessao/regras-de-desconto": json({ desconto: false, cortesia: false, brinde: false }),
    "GET /comandas": json([c1, c2]),
    "GET /comandas/c1/conta": json(conta),
    "GET /comandas/c1/itens": json({ itens: ITENS, consumoCentavos: 5500 }),
    "GET /cardapio/produtos": json([]),
    "GET /cardapio/categorias": json([]),
    "GET /cardapio/produtos/frequentes": json([]),
    "GET /cardapio/produtos/favoritos": json([]),
    ...extra,
  } as Rotas as never);
  await renderEm("/salao/comanda/c1");
  await screen.findByRole("heading", { level: 2, name: "Ana" });
  return chamadas;
}

async function abrirMenuEEscolher(user: ReturnType<typeof userEvent.setup>, acao: string) {
  await user.click(await screen.findByRole("button", { name: "Mais" }));
  await user.click(await screen.findByRole("button", { name: acao }));
}

const corpoDe = (chamadas: { method: string; path: string; body?: unknown }[], fim: string) =>
  chamadas.find((c) => c.method === "POST" && c.path.endsWith(fim))?.body;

const ultimoCorpoDe = (chamadas: { method: string; path: string; body?: unknown }[], fim: string) =>
  chamadas.filter((c) => c.method === "POST" && c.path.endsWith(fim)).at(-1)?.body;

const movimento = (origemVersao: number, destino = c2, itensMovidos = 1) =>
  jsonDoContrato("post", "/comandas/{comandaId}/itens/transferir", 200, {
    origem: { ...c1, versao: origemVersao },
    destino: { ...destino, versao: destino.versao + 1 },
    itensMovidos,
  } as never);

describe("menu visibility", () => {
  it("shows each action only to who has its permission", async () => {
    await abrir(["comanda:ver", "comanda:dividir"]);
    const user = userEvent.setup();
    await user.click(await screen.findByRole("button", { name: "Mais" }));
    expect(screen.getByRole("button", { name: "Dividir a conta" })).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Transferir itens" })).toBeNull();
    expect(screen.queryByRole("button", { name: "Separar itens" })).toBeNull();
    expect(screen.queryByRole("button", { name: "Juntar comandas" })).toBeNull();
  });

  it("shows none of them (and no Mais button) without the three permissions", async () => {
    await abrir(["comanda:ver"]);
    await screen.findByText(/Chopp 300ml/);
    expect(screen.queryByRole("button", { name: "Mais" })).toBeNull();
  });

  it("offers transfer and separate with comanda:transferir-item and merge with comanda:juntar", async () => {
    await abrir(["comanda:ver", "comanda:transferir-item", "comanda:juntar"]);
    const user = userEvent.setup();
    await user.click(await screen.findByRole("button", { name: "Mais" }));
    for (const nome of ["Transferir itens", "Separar itens", "Juntar comandas"]) {
      expect(screen.getByRole("button", { name: nome })).toBeInTheDocument();
    }

    expect(screen.queryByRole("button", { name: "Dividir a conta" })).toBeNull();
  });
});

describe("transfer items", () => {
  it("ticks items, picks the destination and sends both versions; nothing goes to the queue", async () => {
    const chamadas = await abrir(TODAS, { "POST /comandas/c1/itens/transferir": movimento(3) });
    const user = userEvent.setup();
    await abrirMenuEEscolher(user, "Transferir itens");
    const folha = within(await screen.findByRole("dialog", { name: "Transferir itens" }));
    expect(folha.getByRole("button", { name: "Transferir" })).toBeDisabled();

    await user.click(folha.getByRole("checkbox", { name: /Porção de fritas/ }));
    expect(folha.getByRole("button", { name: "Transferir" })).toBeDisabled();
    await user.click(folha.getByRole("radio", { name: /Beto/ }));
    expect(folha.getByText(/1 item · R\$\s*30,00 para Beto/)).toBeInTheDocument();
    await user.click(folha.getByRole("button", { name: "Transferir" }));

    await waitFor(() =>
      expect(corpoDe(chamadas, "/itens/transferir")).toEqual({
        versao: 2,
        destinoId: "c2",
        destinoVersao: 5,
        itemIds: ["i2"],
      }),
    );
    await waitFor(() => expect(screen.queryByRole("dialog", { name: "Transferir itens" })).toBeNull());
    expect(await screen.findByText("1 item transferido para Beto.")).toBeInTheDocument();
    expect(lerFila()).toHaveLength(0);
  });

  it("on a 409 refreshes the numbers, says what happened and KEEPS the ticked items", async () => {
    let tentativa = 0;
    const chamadas = await abrir(TODAS, {
      "POST /comandas/c1/itens/transferir": () => {
        tentativa += 1;
        return tentativa === 1
          ? jsonDoContrato("post", "/comandas/{comandaId}/itens/transferir", 409, {
              codigo: "versao-desatualizada",
              comandaId: "c2",
              versaoAtual: 6,
            })
          : movimento(3);
      },
    });
    const user = userEvent.setup();
    await abrirMenuEEscolher(user, "Transferir itens");
    const folha = within(await screen.findByRole("dialog", { name: "Transferir itens" }));
    await user.click(folha.getByRole("checkbox", { name: /Porção de fritas/ }));
    await user.click(folha.getByRole("radio", { name: /Beto/ }));
    const antes = chamadas.filter((c) => c.method === "GET" && c.path === "/comandas").length;
    await user.click(folha.getByRole("button", { name: "Transferir" }));

    expect(await folha.findByRole("alert")).toHaveTextContent("outro aparelho");
    await waitFor(() =>
      expect(chamadas.filter((c) => c.method === "GET" && c.path === "/comandas").length).toBeGreaterThan(antes),
    );
    expect(folha.getByRole("checkbox", { name: /Porção de fritas/ })).toBeChecked();
    expect(folha.getByRole("radio", { name: /Beto/ })).toBeChecked();
    await user.click(folha.getByRole("button", { name: "Transferir" }));
    await waitFor(() => expect(tentativa).toBe(2));
  });

  it.each([
    ["item-com-desconto", "tem desconto"],
    ["comanda-nao-aberta", "não está mais aberta"],
    ["valor-ja-recebido", "já foi recebido"],
  ])("explains %s and keeps the sheet open", async (codigo, trecho) => {
    await abrir(TODAS, {
      "POST /comandas/c1/itens/transferir": jsonDoContrato("post", "/comandas/{comandaId}/itens/transferir", 409, {
        codigo,
        comandaId: "c1",
        versaoAtual: 2,
      }),
    });
    const user = userEvent.setup();
    await abrirMenuEEscolher(user, "Transferir itens");
    const folha = within(await screen.findByRole("dialog", { name: "Transferir itens" }));
    await user.click(folha.getByRole("checkbox", { name: /Chopp/ }));
    await user.click(folha.getByRole("radio", { name: /Beto/ }));
    await user.click(folha.getByRole("button", { name: "Transferir" }));
    expect(await folha.findByRole("alert")).toHaveTextContent(trecho);
    expect(folha.getByRole("checkbox", { name: /Chopp/ })).toBeChecked();
  });

  it("answers 403 without saying why", async () => {
    await abrir(TODAS, {
      "POST /comandas/c1/itens/transferir": new Response(null, { status: 403 }),
    });
    const user = userEvent.setup();
    await abrirMenuEEscolher(user, "Transferir itens");
    const folha = within(await screen.findByRole("dialog", { name: "Transferir itens" }));
    await user.click(folha.getByRole("checkbox", { name: /Chopp/ }));
    await user.click(folha.getByRole("radio", { name: /Beto/ }));
    await user.click(folha.getByRole("button", { name: "Transferir" }));
    expect(await folha.findByRole("alert")).toHaveTextContent("Você não tem permissão para transferir itens.");
  });

  it("offline: explains it needs the server, blocks the confirm and queues nothing", async () => {
    await abrir();
    Object.defineProperty(window.navigator, "onLine", { configurable: true, value: false });
    const user = userEvent.setup();
    await abrirMenuEEscolher(user, "Transferir itens");
    const folha = within(await screen.findByRole("dialog", { name: "Transferir itens" }));
    await user.click(folha.getByRole("checkbox", { name: /Chopp/ }));
    await user.click(folha.getByRole("radio", { name: /Beto/ }));
    expect(folha.getByRole("status")).toHaveTextContent(
      "Sem conexão. Transferir itens precisa do servidor e não fica na fila.",
    );
    expect(folha.getByRole("button", { name: "Transferir" })).toBeDisabled();
    expect(lerFila()).toHaveLength(0);
  });

  it("says so when no other comanda is open", async () => {
    await abrir(TODAS, { "GET /comandas": json([c1]) });
    const user = userEvent.setup();
    await abrirMenuEEscolher(user, "Transferir itens");
    expect(await screen.findByText("Não há outra comanda aberta para receber os itens.")).toBeInTheDocument();
  });
});

describe("separate items", () => {
  it("sends the ticked items with the optional name and tells the new comanda", async () => {
    const chamadas = await abrir(TODAS, {
      "POST /comandas/c1/itens/separar": jsonDoContrato("post", "/comandas/{comandaId}/itens/separar", 201, {
        origem: { ...c1, versao: 3 },
        destino: { ...c2, id: "c9", numeroTexto: "#9", identificacao: "Primos", versao: 2 },
        itensMovidos: 1,
      } as never),
    });
    const user = userEvent.setup();
    await abrirMenuEEscolher(user, "Separar itens");
    const folha = within(await screen.findByRole("dialog", { name: "Separar itens" }));
    await user.click(folha.getByRole("checkbox", { name: /Porção de fritas/ }));
    await user.type(folha.getByLabelText("Nome da comanda nova (opcional)"), "Primos");
    await user.click(folha.getByRole("button", { name: "Separar" }));
    await waitFor(() =>
      expect(corpoDe(chamadas, "/itens/separar")).toEqual({ versao: 2, itemIds: ["i2"], identificacao: "Primos" }),
    );
    expect(await screen.findByText("1 item separado na comanda #9.")).toBeInTheDocument();
  });
});

describe("merge comandas", () => {
  it("asks for an explicit confirmation before merging and sends both versions", async () => {
    const chamadas = await abrir(TODAS, {
      "POST /comandas/c1/juntar": jsonDoContrato("post", "/comandas/{comandaId}/juntar", 200, {
        origem: { ...c1, versao: 3 },
        destino: { ...c2, estado: "juntada", versao: 6 },
        itensMovidos: 2,
      } as never),
    });
    const user = userEvent.setup();
    await abrirMenuEEscolher(user, "Juntar comandas");
    const folha = within(await screen.findByRole("dialog", { name: "Juntar comandas" }));
    expect(folha.getByRole("button", { name: "Continuar" })).toBeDisabled();
    await user.click(folha.getByRole("radio", { name: /Beto/ }));
    await user.click(folha.getByRole("button", { name: "Continuar" }));
    expect(corpoDe(chamadas, "/juntar")).toBeUndefined();
    expect(folha.getByText("Não dá para desfazer")).toBeInTheDocument();
    await user.click(folha.getByRole("button", { name: "Juntar comandas" }));
    await waitFor(() => expect(corpoDe(chamadas, "/juntar")).toEqual({ versao: 2, origemId: "c2", origemVersao: 5 }));
    expect(await screen.findByText(/Beto \(#8\) foi juntada em Ana/)).toBeInTheDocument();
  });

  it("explains a refusal (comanda with discount) and goes back to the list with the choice kept", async () => {
    await abrir(TODAS, {
      "POST /comandas/c1/juntar": jsonDoContrato("post", "/comandas/{comandaId}/juntar", 409, {
        codigo: "comanda-com-desconto",
        comandaId: "c2",
        versaoAtual: 5,
      }),
    });
    const user = userEvent.setup();
    await abrirMenuEEscolher(user, "Juntar comandas");
    const folha = within(await screen.findByRole("dialog", { name: "Juntar comandas" }));
    await user.click(folha.getByRole("radio", { name: /Beto/ }));
    await user.click(folha.getByRole("button", { name: "Continuar" }));
    await user.click(folha.getByRole("button", { name: "Juntar comandas" }));
    expect(await folha.findByRole("alert")).toHaveTextContent("tem desconto");
    expect(folha.getByRole("radio", { name: /Beto/ })).toBeChecked();
  });
});

describe("split the bill", () => {
  const previa = (modo: string, partes: number[], versao = 2) =>
    jsonDoContrato("post", "/comandas/{comandaId}/divisao/previa", 200, {
      modo,
      comandaVersao: versao,
      totalCentavos: 6050,
      taxaServicoCentavos: 550,
      gorjetaCentavos: 0,
      partes: partes.map((v, i) => ({
        numero: i + 1,
        assento: null,
        itemIds: [],
        consumoCentavos: v - Math.round(v / 11),
        taxaServicoCentavos: Math.round(v / 11),
        totalCentavos: v,
        gorjetaCentavos: 0,
        aPagarCentavos: v,
      })),
    });
  const dividida = (partes: number[]) =>
    jsonDoContrato("post", "/comandas/{comandaId}/divisao", 201, {
      id: "d1",
      comandaId: "c1",
      modo: "iguais",
      comandaVersao: 3,
      vigente: true,
      totalCentavos: 6050,
      taxaServicoCentavos: 550,
      gorjetaCentavos: 0,
      criadoEm: new Date().toISOString(),
      partes: partes.map((v, i) => ({
        numero: i + 1,
        assento: null,
        itemIds: [],
        consumoCentavos: v,
        taxaServicoCentavos: 0,
        totalCentavos: v,
        gorjetaCentavos: 0,
        aPagarCentavos: v,
      })),
    });

  it("equal parts in 4 taps: Mais, Dividir a conta, + (3 parts), Confirmar, with the server preview before confirming", async () => {
    const chamadas = await abrir(TODAS, {
      "POST /comandas/c1/divisao/previa": (c: { body: { partes: number } }) =>
        c.body.partes === 3 ? previa("iguais", [2017, 2017, 2016]) : previa("iguais", [3025, 3025]),
      "POST /comandas/c1/divisao": dividida([2017, 2017, 2016]),
    });
    const user = userEvent.setup();
    let toques = 0;
    const tocar = async (el: HTMLElement) => {
      toques += 1;
      await user.click(el);
    };
    await tocar(await screen.findByRole("button", { name: "Mais" }));
    await tocar(await screen.findByRole("button", { name: "Dividir a conta" }));
    const folha = within(await screen.findByRole("dialog", { name: "Dividir a conta" }));
    expect(await folha.findByText(/Parte 2/)).toBeInTheDocument();
    await tocar(folha.getByRole("button", { name: "Aumentar Partes" }));
    const lista = await folha.findByRole("list", { name: "Partes da divisão" });
    await waitFor(() => expect(within(lista).getAllByRole("listitem")).toHaveLength(3));
    expect(lista).toHaveTextContent(/R\$\s*20,17/);
    expect(corpoDe(chamadas, "/divisao")).toBeUndefined();
    await tocar(folha.getByRole("button", { name: "Confirmar divisão" }));

    await waitFor(() =>
      expect(corpoDe(chamadas, "/comandas/c1/divisao")).toMatchObject({
        versao: 2,
        pedido: { modo: "iguais", partes: 3 },
      }),
    );
    expect(await screen.findByRole("dialog", { name: "Conta dividida" })).toBeInTheDocument();
    expect(toques).toBe(4);
    expect(lerFila()).toHaveLength(0);
  });

  it("the preview shows loading, then the server's parts", async () => {
    let liberar: (r: Response) => void = () => undefined;
    await abrir(TODAS, {
      "POST /comandas/c1/divisao/previa": () => new Promise<Response>((r) => (liberar = r)),
    });
    const user = userEvent.setup();
    await abrirMenuEEscolher(user, "Dividir a conta");
    const folha = within(await screen.findByRole("dialog", { name: "Dividir a conta" }));
    expect(await folha.findByText("Calculando a divisão…")).toBeInTheDocument();
    expect(folha.getByRole("button", { name: "Confirmar divisão" })).toBeDisabled();
    liberar(previa("iguais", [3025, 3025]));
    expect(await folha.findByText("Parte 1")).toBeInTheDocument();
    expect(folha.getByRole("button", { name: "Confirmar divisão" })).toBeEnabled();
  });

  it("a failed preview says so and offers to try again", async () => {
    let falhar = true;
    await abrir(TODAS, {
      "POST /comandas/c1/divisao/previa": () =>
        falhar ? new Response(null, { status: 500 }) : previa("iguais", [3025, 3025]),
    });
    const user = userEvent.setup();
    await abrirMenuEEscolher(user, "Dividir a conta");
    const folha = within(await screen.findByRole("dialog", { name: "Dividir a conta" }));
    expect(await folha.findByRole("alert")).toHaveTextContent("Não foi possível");
    expect(folha.getByRole("button", { name: "Confirmar divisão" })).toBeDisabled();
    falhar = false;
    await user.click(folha.getByRole("button", { name: "Tentar novamente" }));
    expect(await folha.findByText("Parte 1")).toBeInTheDocument();
  });

  it("seat mode: the server refusing it (no seats) is explained in words", async () => {
    await abrir(TODAS, {
      "POST /comandas/c1/divisao/previa": (c: { body: { modo: string } }) =>
        c.body.modo === "assento"
          ? jsonDoContrato("post", "/comandas/{comandaId}/divisao/previa", 400, { codigo: "invalido" })
          : previa("iguais", [3025, 3025]),
    });
    const user = userEvent.setup();
    await abrirMenuEEscolher(user, "Dividir a conta");
    const folha = within(await screen.findByRole("dialog", { name: "Dividir a conta" }));
    await user.click(folha.getByRole("button", { name: "Por assento" }));
    expect(await folha.findByRole("alert")).toHaveTextContent("assento");
    expect(folha.getByRole("button", { name: "Confirmar divisão" })).toBeDisabled();
    await user.click(folha.getByRole("button", { name: "Dividir por item" }));
    expect(folha.getByRole("button", { name: "Por item" })).toHaveAttribute("aria-pressed", "true");
  });

  it("item mode assigns each item to a part and sends the groups", async () => {
    const chamadas = await abrir(TODAS, {
      "POST /comandas/c1/divisao/previa": previa("item", [2750, 3300]),
      "POST /comandas/c1/divisao": dividida([2750, 3300]),
    });
    const user = userEvent.setup();
    await abrirMenuEEscolher(user, "Dividir a conta");
    const folha = within(await screen.findByRole("dialog", { name: "Dividir a conta" }));
    await user.click(folha.getByRole("button", { name: "Por item" }));
    await waitFor(() =>
      expect(ultimoCorpoDe(chamadas, "/divisao/previa")).toMatchObject({ modo: "item", grupos: [["i1"], ["i2"]] }),
    );
    await user.selectOptions(folha.getByLabelText("Parte de Porção de fritas"), "1");
    expect(await folha.findByText(/pelo menos um item/)).toBeInTheDocument();
    expect(folha.getByRole("button", { name: "Confirmar divisão" })).toBeDisabled();
  });

  it("free values: shows what is missing until they add up, then asks the server for the preview", async () => {
    const chamadas = await abrir(TODAS, {
      "POST /comandas/c1/divisao/previa": previa("valores", [4000, 2050]),
    });
    const user = userEvent.setup();
    await abrirMenuEEscolher(user, "Dividir a conta");
    const folha = within(await screen.findByRole("dialog", { name: "Dividir a conta" }));
    await user.click(folha.getByRole("button", { name: "Valores livres" }));
    const campo = folha.getByLabelText("Parte 1");
    await user.clear(campo);
    await user.type(campo, "4000");
    expect(await folha.findByText(/Faltam|Passou/)).toBeInTheDocument();
    const outro = folha.getByLabelText("Parte 2");
    await user.clear(outro);
    await user.type(outro, "2050");
    await waitFor(() =>
      expect(ultimoCorpoDe(chamadas, "/divisao/previa")).toMatchObject({ modo: "valores", valores: [4000, 2050] }),
    );
  });

  it("percent: the starting values add up to 100 and are sent as numbers", async () => {
    const chamadas = await abrir(TODAS, { "POST /comandas/c1/divisao/previa": previa("percentual", [3025, 3025]) });
    const user = userEvent.setup();
    await abrirMenuEEscolher(user, "Dividir a conta");
    const folha = within(await screen.findByRole("dialog", { name: "Dividir a conta" }));
    await user.click(folha.getByRole("button", { name: "Percentual" }));
    await waitFor(() =>
      expect(ultimoCorpoDe(chamadas, "/divisao/previa")).toMatchObject({ modo: "percentual", percentuais: [50, 50] }),
    );
  });

  it("on a 409 the numbers and the preview refresh, the form stays, and a second confirm sends the new version", async () => {
    let versaoDaPrevia = 2;
    let tentativas = 0;
    const chamadas = await abrir(TODAS, {
      "POST /comandas/c1/divisao/previa": () => previa("iguais", [3025, 3025], versaoDaPrevia),
      "POST /comandas/c1/divisao": () => {
        tentativas += 1;
        if (tentativas === 1) {
          versaoDaPrevia = 3;
          return jsonDoContrato("post", "/comandas/{comandaId}/divisao", 409, {
            codigo: "versao-desatualizada",
            versaoAtual: 3,
          });
        }

        return dividida([3025, 3025]);
      },
    });
    const user = userEvent.setup();
    await abrirMenuEEscolher(user, "Dividir a conta");
    const folha = within(await screen.findByRole("dialog", { name: "Dividir a conta" }));
    await user.click(await folha.findByRole("button", { name: "Aumentar Partes" }));
    await user.click(await folha.findByRole("button", { name: "Diminuir Partes" }));
    await waitFor(() => expect(folha.getByRole("button", { name: "Confirmar divisão" })).toBeEnabled());
    await user.click(folha.getByRole("button", { name: "Confirmar divisão" }));
    expect(await folha.findByRole("alert")).toHaveTextContent("outro aparelho");
    await waitFor(() => expect(folha.getByRole("button", { name: "Confirmar divisão" })).toBeEnabled());
    await user.click(folha.getByRole("button", { name: "Confirmar divisão" }));
    await waitFor(() => expect(tentativas).toBe(2));
    const envios = chamadas.filter((c) => c.method === "POST" && c.path === "/comandas/c1/divisao");
    const corpos = envios.map((e) => e.body as { versao: number; divisaoId: string });
    expect(corpos[1]?.versao).toBe(3);
    // The same request keeps the same split id (a retry is never a second split).
    expect(corpos[1]?.divisaoId).toBe(corpos[0]?.divisaoId);
  });

  it("explains a comanda that already received payment and keeps the sheet", async () => {
    await abrir(TODAS, {
      "POST /comandas/c1/divisao/previa": previa("iguais", [3025, 3025]),
      "POST /comandas/c1/divisao": jsonDoContrato("post", "/comandas/{comandaId}/divisao", 409, {
        codigo: "valor-ja-recebido",
      }),
    });
    const user = userEvent.setup();
    await abrirMenuEEscolher(user, "Dividir a conta");
    const folha = within(await screen.findByRole("dialog", { name: "Dividir a conta" }));
    await waitFor(() => expect(folha.getByRole("button", { name: "Confirmar divisão" })).toBeEnabled());
    await user.click(folha.getByRole("button", { name: "Confirmar divisão" }));
    expect(await folha.findByRole("alert")).toHaveTextContent("recebeu pagamento");
  });

  it("answers 403 without saying why", async () => {
    await abrir(TODAS, {
      "POST /comandas/c1/divisao/previa": previa("iguais", [3025, 3025]),
      "POST /comandas/c1/divisao": new Response(null, { status: 403 }),
    });
    const user = userEvent.setup();
    await abrirMenuEEscolher(user, "Dividir a conta");
    const folha = within(await screen.findByRole("dialog", { name: "Dividir a conta" }));
    await waitFor(() => expect(folha.getByRole("button", { name: "Confirmar divisão" })).toBeEnabled());
    await user.click(folha.getByRole("button", { name: "Confirmar divisão" }));
    expect(await folha.findByRole("alert")).toHaveTextContent("Você não tem permissão para dividir a conta.");
  });

  it("offline: explains it needs the server, asks nothing and blocks the confirm", async () => {
    const chamadas = await abrir();
    Object.defineProperty(window.navigator, "onLine", { configurable: true, value: false });
    const user = userEvent.setup();
    await abrirMenuEEscolher(user, "Dividir a conta");
    const folha = within(await screen.findByRole("dialog", { name: "Dividir a conta" }));
    expect(folha.getByRole("status")).toHaveTextContent(
      "Sem conexão. Dividir a conta precisa do servidor e não fica na fila.",
    );
    expect(folha.getByRole("button", { name: "Confirmar divisão" })).toBeDisabled();
    expect(chamadas.some((c) => c.path.endsWith("/divisao/previa"))).toBe(false);
    expect(lerFila()).toHaveLength(0);
  });
});
