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

const PERMISSOES = ["comanda:ver", "comanda:cancelar-item", "mesa:ver", "produto:ver"];
const comandaJson = {
  id: "c1",
  unidadeId: "u1",
  numero: 7,
  numeroTexto: "#7",
  identificacao: "Ana",
  mesaId: null,
  canal: "salao",
  garcomId: "g1",
  pessoas: 2,
  estado: "aberta",
  taxaServicoPercentual: 10,
  taxaServicoRemovida: false,
  versao: 2,
  abertaEm: new Date(Date.now() - 5 * 60_000).toISOString(),
  observacao: null,
};
const item = {
  id: "i1",
  rodadaId: "r1",
  posicao: 1,
  produtoId: "p1",
  produtoNome: "Chopp 300ml",
  precoCentavos: 1250,
  quantidade: 2,
  observacao: null,
  estado: "lancado",
  motivoCancelamento: null,
  canceladoPor: null,
  canceladoEm: null,
  opcoes: [],
  totalCentavos: 2500,
  tipo: "normal",
  motivoDoTipo: null,
  valorDeTabelaCentavos: 2500,
};
const conta = {
  consumoCentavos: 2500,
  taxaServicoCentavos: 250,
  totalCentavos: 2750,
  pagoCentavos: 0,
  saldoCentavos: 2750,
  taxaServicoRemovida: false,
  taxaServicoPercentual: 10,
  versao: 2,
  estado: "aberta",
  descontoCentavos: 0,
};

type Rotas = Record<string, Response | ((c: never) => Response | Promise<Response>)>;

async function abrir(extra: Rotas = {}, itens: unknown[] = [item], contaAtual: unknown = conta) {
  const chamadas = stubRede({
    "POST /refresh": json(sessaoJson()),
    "GET /sessao/permissoes": json({ permissoes: PERMISSOES }),
    "GET /sessao/regras-de-desconto": json({ desconto: true, cortesia: true, brinde: true }),
    "GET /comandas": json([comandaJson]),
    "GET /comandas/c1/conta": json(contaAtual),
    "GET /comandas/c1/itens": json({ itens, consumoCentavos: 2500 }),
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

const corpoDe = (chamadas: { method: string; path: string; body?: unknown }[], fim: string) =>
  chamadas.find((c) => c.method === "POST" && c.path.endsWith(fim))?.body;

async function digitar(user: ReturnType<typeof userEvent.setup>, folha: ReturnType<typeof within>, texto: string) {
  for (const d of texto) {
    await user.click(folha.getByRole("button", { name: d }));
  }
}

const aplicado = (conta2: Partial<typeof conta> = {}) =>
  jsonDoContrato("post", "/comandas/{comandaId}/descontos", 201, {
    desconto: {
      id: "d1",
      comandaId: "c1",
      itemId: "i1",
      forma: "percentual",
      percentual: 10,
      valorCentavos: null,
      aplicadoCentavos: 250,
      motivo: "Cliente frequente",
    },
    conta: { ...conta, ...conta2, descontoCentavos: 250 },
  } as never);

describe("discount on an item", () => {
  it("types a percent, shows the new total, asks for a reason and sends the percent with the version", async () => {
    const chamadas = await abrir({ "POST /comandas/c1/descontos": aplicado({ totalCentavos: 2475 }) });
    const user = userEvent.setup();

    await user.click(await screen.findByRole("button", { name: "Desconto em Chopp 300ml" }));
    const folha = await screen.findByRole("dialog", { name: "Desconto, cortesia ou brinde" });
    const f = within(folha);
    expect(f.getByRole("button", { name: "Aplicar desconto" })).toBeDisabled();
    await digitar(user, f, "10");

    const previa = f.getByLabelText("Prévia da conta");
    expect(previa).toHaveTextContent("Tira");
    expect(previa).toHaveTextContent(/R\$\s*2,50/);
    expect(previa).toHaveTextContent(/Novo total\s*R\$\s*24,75/);
    expect(f.getByRole("button", { name: "Aplicar desconto" })).toBeDisabled();
    await user.click(f.getByRole("button", { name: "Cliente frequente" }));
    await user.click(f.getByRole("button", { name: "Aplicar desconto" }));

    await waitFor(() =>
      expect(corpoDe(chamadas, "/descontos")).toEqual({
        versao: 2,
        itemId: "i1",
        percentual: 10,
        valorCentavos: null,
        motivo: "Cliente frequente",
      }),
    );
    await waitFor(() => expect(screen.queryByRole("dialog", { name: "Desconto, cortesia ou brinde" })).toBeNull());
    expect(lerFila()).toHaveLength(0);
  });

  it('sends a value in cents when the form is value, and "Outro" needs its own words', async () => {
    const chamadas = await abrir({ "POST /comandas/c1/descontos": aplicado() });
    const user = userEvent.setup();

    await user.click(await screen.findByRole("button", { name: "Desconto em Chopp 300ml" }));
    const f = within(await screen.findByRole("dialog", { name: "Desconto, cortesia ou brinde" }));
    await user.click(f.getByRole("button", { name: "Valor" }));
    await digitar(user, f, "500");
    await user.click(f.getByRole("button", { name: "Outro" }));
    expect(f.getByRole("button", { name: "Aplicar desconto" })).toBeDisabled();
    await user.type(f.getByLabelText("Qual o motivo?"), "Vizinho");
    await user.click(f.getByRole("button", { name: "Aplicar desconto" }));

    await waitFor(() =>
      expect(corpoDe(chamadas, "/descontos")).toMatchObject({
        percentual: null,
        valorCentavos: 500,
        motivo: "Vizinho",
      }),
    );
  });

  it("types a decimal percent with a comma, previews and sends it as a decimal, two decimals at most", async () => {
    const chamadas = await abrir({ "POST /comandas/c1/descontos": aplicado({ totalCentavos: 2_575 }) });
    const user = userEvent.setup();

    await user.click(await screen.findByRole("button", { name: "Desconto em Chopp 300ml" }));
    const f = within(await screen.findByRole("dialog", { name: "Desconto, cortesia ou brinde" }));
    await user.click(f.getByRole("button", { name: "Vírgula" }));
    await digitar(user, f, "755");
    expect(f.getByLabelText("Desconto digitado")).toHaveTextContent("0,75%");
    // the third decimal was refused; erase "0,75" key by key (the comma included) and type 7,5
    for (let i = 0; i < 4; i += 1) {
      await user.click(f.getByRole("button", { name: "Apagar" }));
    }

    await digitar(user, f, "7");
    await user.click(f.getByRole("button", { name: "Vírgula" }));
    await digitar(user, f, "5");
    expect(f.getByLabelText("Desconto digitado")).toHaveTextContent("7,5%");
    expect(f.getByLabelText("Prévia da conta")).toHaveTextContent(/Tira\s*R\$\s*1,88/);
    await user.click(f.getByRole("button", { name: "Cliente frequente" }));
    await user.click(f.getByRole("button", { name: "Aplicar desconto" }));

    await waitFor(() =>
      expect(corpoDe(chamadas, "/descontos")).toMatchObject({ percentual: 7.5, valorCentavos: null }),
    );
  });

  it("has no comma in value mode and refuses a percent that is zero", async () => {
    await abrir();
    const user = userEvent.setup();

    await user.click(await screen.findByRole("button", { name: "Desconto em Chopp 300ml" }));
    const f = within(await screen.findByRole("dialog", { name: "Desconto, cortesia ou brinde" }));
    await user.click(f.getByRole("button", { name: "Vírgula" }));
    await digitar(user, f, "00");
    await user.click(f.getByRole("button", { name: "Cliente frequente" }));
    expect(f.getByRole("button", { name: "Aplicar desconto" })).toBeDisabled();
    await user.click(f.getByRole("button", { name: "Valor" }));
    expect(f.queryByRole("button", { name: "Vírgula" })).toBeNull();
  });

  it("refuses more than 100 percent before sending", async () => {
    await abrir();
    const user = userEvent.setup();

    await user.click(await screen.findByRole("button", { name: "Desconto em Chopp 300ml" }));
    const f = within(await screen.findByRole("dialog", { name: "Desconto, cortesia ou brinde" }));
    await digitar(user, f, "150");
    await user.click(f.getByRole("button", { name: "Cliente frequente" }));

    expect(f.getByText("Use de 0,01% a 100%.")).toBeInTheDocument();
    expect(f.getByRole("button", { name: "Aplicar desconto" })).toBeDisabled();
  });

  it("on 403 says the value is not allowed, never names the limit, keeps the sheet and the typed value", async () => {
    await abrir({ "POST /comandas/c1/descontos": new Response(null, { status: 403 }) });
    const user = userEvent.setup();

    await user.click(await screen.findByRole("button", { name: "Desconto em Chopp 300ml" }));
    const f = within(await screen.findByRole("dialog", { name: "Desconto, cortesia ou brinde" }));
    await digitar(user, f, "90");
    await user.click(f.getByRole("button", { name: "Cliente frequente" }));
    await user.click(f.getByRole("button", { name: "Aplicar desconto" }));

    expect(await f.findByText("Sem permissão para este valor.")).toBeInTheDocument();
    expect(f.getByLabelText("Desconto digitado")).toHaveTextContent("90%");
    expect(screen.getByRole("dialog", { name: "Desconto, cortesia ou brinde" })).not.toHaveTextContent(/limite/i);
  });

  it("on a stale version refreshes the bill and tells the person to apply again", async () => {
    await abrir({
      "POST /comandas/c1/descontos": json({ codigo: "versao-desatualizada", versaoAtual: 5 }, { status: 409 }),
    });
    const user = userEvent.setup();

    await user.click(await screen.findByRole("button", { name: "Desconto em Chopp 300ml" }));
    const f = within(await screen.findByRole("dialog", { name: "Desconto, cortesia ou brinde" }));
    await digitar(user, f, "10");
    await user.click(f.getByRole("button", { name: "Cliente frequente" }));
    await user.click(f.getByRole("button", { name: "Aplicar desconto" }));

    expect(await f.findByText(/foi alterada em outro aparelho/)).toBeInTheDocument();
  });
});

describe("courtesy and gift", () => {
  it("gives a courtesy with the reason and sends only version and reason", async () => {
    const chamadas = await abrir({
      "POST /comandas/c1/itens/i1/cortesia": json({
        item: { ...item, tipo: "cortesia" },
        conta: { ...conta, consumoCentavos: 0, totalCentavos: 0, versao: 3 },
      }),
    });
    const user = userEvent.setup();

    await user.click(await screen.findByRole("button", { name: "Desconto em Chopp 300ml" }));
    const f = within(await screen.findByRole("dialog", { name: "Desconto, cortesia ou brinde" }));
    await user.click(f.getByRole("button", { name: "Cortesia" }));
    expect(f.getByLabelText("Prévia da conta")).toHaveTextContent(/Novo total\s*R\$\s*0,00/);
    await user.click(f.getByRole("button", { name: "Aniversário" }));
    await user.click(f.getByRole("button", { name: "Dar cortesia" }));

    await waitFor(() => expect(corpoDe(chamadas, "/itens/i1/cortesia")).toEqual({ versao: 2, motivo: "Aniversário" }));
  });

  it("gives a gift through its own route", async () => {
    const chamadas = await abrir({
      "POST /comandas/c1/itens/i1/brinde": json({ item: { ...item, tipo: "brinde" }, conta: { ...conta, versao: 3 } }),
    });
    const user = userEvent.setup();

    await user.click(await screen.findByRole("button", { name: "Desconto em Chopp 300ml" }));
    const f = within(await screen.findByRole("dialog", { name: "Desconto, cortesia ou brinde" }));
    await user.click(f.getByRole("button", { name: "Brinde" }));
    await user.click(f.getByRole("button", { name: "Decisão da gerência" }));
    await user.click(f.getByRole("button", { name: "Dar brinde" }));

    await waitFor(() =>
      expect(corpoDe(chamadas, "/itens/i1/brinde")).toEqual({ versao: 2, motivo: "Decisão da gerência" }),
    );
  });

  it("shows the item as courtesy in the bill, charged zero, without a discount button", async () => {
    await abrir({}, [{ ...item, tipo: "cortesia", motivoDoTipo: "Aniversário" }]);

    const linha = (await screen.findByText("Chopp 300ml")).closest("li");
    expect(linha).toHaveTextContent("Cortesia");
    expect(linha).toHaveTextContent("Aniversário");
    expect(linha).toHaveTextContent(/R\$\s*0,00/);
    expect(screen.queryByRole("button", { name: "Desconto em Chopp 300ml" })).not.toBeInTheDocument();
  });

  it("shows a gift as brinde", async () => {
    await abrir({}, [{ ...item, tipo: "brinde", motivoDoTipo: "Promoção" }]);

    const linha = (await screen.findByText("Chopp 300ml")).closest("li");
    expect(linha).toHaveTextContent("Brinde");
    expect(linha).toHaveTextContent("Promoção");
  });
});

describe("discount on the whole comanda", () => {
  it("is in the menu, offers only the discount (no courtesy or gift) and sends no item", async () => {
    const chamadas = await abrir({ "POST /comandas/c1/descontos": aplicado() });
    const user = userEvent.setup();

    await user.click(await screen.findByRole("button", { name: "Mais" }));
    await user.click(await screen.findByRole("button", { name: "Desconto na comanda" }));
    const f = within(await screen.findByRole("dialog", { name: "Desconto na comanda" }));
    expect(f.queryByRole("button", { name: "Cortesia" })).not.toBeInTheDocument();
    await digitar(user, f, "5");
    await user.click(f.getByRole("button", { name: "Reclamação do cliente" }));
    await user.click(f.getByRole("button", { name: "Aplicar desconto" }));

    await waitFor(() =>
      expect(corpoDe(chamadas, "/descontos")).toMatchObject({ itemId: null, percentual: 5, valorCentavos: null }),
    );
  });

  it("is not offered once the comanda is not open", async () => {
    await abrir({}, [item], { ...conta, estado: "fechada" });

    await screen.findByText("Chopp 300ml");
    expect(screen.queryByRole("button", { name: "Mais" })).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Desconto em Chopp 300ml" })).not.toBeInTheDocument();
  });
});

describe("what is offered depends on the rules the server says the caller has", () => {
  const regras = (desconto: boolean, cortesia: boolean, brinde: boolean) => ({
    "GET /sessao/regras-de-desconto": json({ desconto, cortesia, brinde }),
  });

  it("offers nothing (no row button, no menu) when the caller has no rule", async () => {
    await abrir(regras(false, false, false));

    await screen.findByText("Chopp 300ml");
    expect(screen.queryByRole("button", { name: "Desconto em Chopp 300ml" })).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Mais" })).not.toBeInTheDocument();
  });

  it("offers on an item only the actions the caller has a rule for", async () => {
    await abrir(regras(false, true, false));
    const user = userEvent.setup();

    await user.click(await screen.findByRole("button", { name: "Desconto em Chopp 300ml" }));
    const f = within(await screen.findByRole("dialog", { name: "Desconto, cortesia ou brinde" }));
    expect(f.getByRole("button", { name: "Cortesia" })).toBeInTheDocument();
    expect(f.queryByRole("button", { name: "Brinde" })).not.toBeInTheDocument();
    expect(f.queryByRole("button", { name: "Desconto" })).not.toBeInTheDocument();
    expect(f.getByRole("button", { name: "Dar cortesia" })).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Mais" })).not.toBeInTheDocument();
  });

  it("offers the discount of the comanda in the menu only with a discount rule", async () => {
    await abrir(regras(false, true, true));

    await screen.findByText("Chopp 300ml");
    expect(screen.queryByRole("button", { name: "Mais" })).not.toBeInTheDocument();
  });
});

describe("needs a connection", () => {
  it("explains it offline, blocks the button and queues nothing", async () => {
    await abrir();
    const user = userEvent.setup();
    Object.defineProperty(window.navigator, "onLine", { configurable: true, value: false });

    await user.click(await screen.findByRole("button", { name: "Desconto em Chopp 300ml" }));
    const f = within(await screen.findByRole("dialog", { name: "Desconto, cortesia ou brinde" }));
    await digitar(user, f, "10");
    await user.click(f.getByRole("button", { name: "Cliente frequente" }));

    expect(f.getByText(/precisam do servidor e não ficam na fila/)).toBeInTheDocument();
    // The reason sits in the pinned footer, next to the disabled button, so scrolling never hides it.
    expect(f.getByText(/precisam do servidor e não ficam na fila/).closest(".f4-sheet-footer")).not.toBeNull();
    expect(f.getByRole("button", { name: "Aplicar desconto" })).toBeDisabled();
    expect(lerFila()).toHaveLength(0);
  });
});

describe("the bill", () => {
  it("shows the discount line on the screen's bill and in the close-bill sheet", async () => {
    const stub = {
      "GET /sessao/permissoes": json({ permissoes: [...PERMISSOES, "comanda:fechar", "comanda:receber-pagamento"] }),
    };
    const brinde = { ...item, id: "i2", produtoNome: "Água", tipo: "brinde", motivoDoTipo: "Promoção" };
    const cortesia = { ...item, id: "i3", produtoNome: "Pastel", tipo: "cortesia", motivoDoTipo: "Aniversário" };
    await abrir(stub, [item, brinde, cortesia], { ...conta, descontoCentavos: 250, totalCentavos: 2475 });
    const user = userEvent.setup();

    expect(await screen.findByLabelText("Desconto da conta")).toHaveTextContent(/R\$\s*2,50/);
    await user.click(await screen.findByRole("button", { name: "Fechar conta" }));
    const folha = within(await screen.findByRole("dialog", { name: /Fechar/ }));
    const contaDaFolha = folha.getByLabelText("Conta");
    expect(contaDaFolha).toHaveTextContent(/Desconto\s*−R\$\s*2,50/);
    expect(contaDaFolha).toHaveTextContent(/Brinde\s*2× Água\s*R\$\s*0,00/);
    expect(contaDaFolha).toHaveTextContent(/Cortesia\s*2× Pastel\s*R\$\s*0,00/);
  });
});
