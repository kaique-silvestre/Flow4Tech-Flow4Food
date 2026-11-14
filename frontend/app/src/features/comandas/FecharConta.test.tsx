import type { components } from "@flow4tech/api-client";
import { onlineManager } from "@tanstack/react-query";
import { screen, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, describe, expect, it, vi } from "vitest";
import { json, jsonDoContrato, renderEm, sessaoJson, stubRede } from "../../test-utils";
import { enfileirarPagamento, lerFila, reiniciarFilaParaTestes } from "../fila/fila";

afterEach(() => {
  vi.unstubAllGlobals();
  Object.defineProperty(window.navigator, "onLine", { configurable: true, value: true });
  onlineManager.setOnline(true);
  reiniciarFilaParaTestes();
});

const CAIXA = [
  "comanda:abrir",
  "comanda:ver",
  "comanda:fechar",
  "comanda:receber-pagamento",
  "caixa:ver",
  "mesa:ver",
  "produto:ver",
];
const comandaJson: components["schemas"]["ComandaDto"] = {
  id: "c1",
  unidadeId: "u1",
  numero: 7,
  numeroTexto: "#7",
  identificacao: "Ana",
  mesaId: null,
  canal: "salao",
  garcomId: "g1",
  pessoas: null,
  estado: "aberta",
  taxaServicoPercentual: 10,
  taxaServicoRemovida: false,
  versao: 2,
  abertaEm: new Date().toISOString(),
  observacao: null,
};
const conta = (extra: Partial<components["schemas"]["ContaDto"]> = {}): components["schemas"]["ContaDto"] => ({
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
  ...extra,
});
const pagamento = (
  extra: Partial<components["schemas"]["PagamentoDto"]> = {},
): components["schemas"]["PagamentoDto"] => ({
  id: "p",
  comandaId: "c1",
  turnoId: "t",
  metodo: "dinheiro",
  recebidoCentavos: 3000,
  aplicadoCentavos: 2750,
  trocoCentavos: 250,
  saldoRestanteCentavos: 0,
  comandaVersao: 3,
  registradoEm: new Date().toISOString(),
  operadorId: "o",
  metodoId: null,
  metodoNome: "Dinheiro",
  gorjetaCentavos: 0,
  estado: "confirmado",
  divisaoId: null,
  divisaoParte: null,
  ...extra,
});

function rotas(extra: Record<string, unknown> = {}, permissoes = CAIXA) {
  return {
    "POST /refresh": json(sessaoJson()),
    "GET /sessao/permissoes": json({ permissoes }),
    "GET /comandas": json([comandaJson]),
    "GET /comandas/c1/conta": json(conta()),
    "GET /comandas/c1/itens": json({ itens: [], consumoCentavos: 2500 }),
    "GET /caixa/turnos/atual": json({ id: "t" }),
    ...extra,
  } as never;
}

async function abrirFolha(extra: Record<string, unknown> = {}, permissoes = CAIXA) {
  const chamadas = stubRede(rotas(extra, permissoes));
  await renderEm("/salao/comanda/c1");
  await screen.findByRole("heading", { level: 2, name: "Ana" });
  await userEvent.click(screen.getByRole("button", { name: "Fechar conta" }));
  const folha = await screen.findByRole("dialog", { name: "Fechar conta" });
  return { chamadas, folha };
}

const n = (texto: string | null | undefined) => (texto ?? "").replace(/\u00a0/g, " ");

/** Types an amount on the keypad of the sheet (the line or the tip that is active). */
async function digitar(folha: HTMLElement, digitos: string) {
  const teclado = within(folha).getByRole("group", { name: "Teclado numérico" });
  for (const d of digitos) {
    await userEvent.click(within(teclado).getByRole("button", { name: d }));
  }
}

describe("close the bill in cash", () => {
  it("without the permission to list the methods it shows consumption, fee, total, the amount to receive and only cash", async () => {
    const { folha } = await abrirFolha();

    const conta_ = within(folha).getByLabelText("Conta");
    expect(conta_.textContent).toContain("Consumo");
    expect(conta_.textContent).toContain("Serviço (10%)");
    expect(n(conta_.textContent)).toContain("R$ 27,50");
    expect(n(within(folha).getByLabelText("Valor a receber").textContent)).toContain("R$ 27,50");
    // Without the permission to list the methods the sheet keeps the old cash-only receipt: the line is already there.
    expect(within(folha).getByRole("button", { name: /^Dinheiro: R\$\s?27,50$/ })).toBeVisible();
    expect(within(folha).getByRole("button", { name: "Receber" })).toBeEnabled();
    expect(within(folha).queryByRole("group", { name: /Método/ })).toBeNull();
    expect(folha.textContent).not.toMatch(/Pix|Crédito|Débito|Voucher|Dividir/i);
  });

  it("shows the change in large type and pays, then closes the bill", async () => {
    const { chamadas, folha } = await abrirFolha({
      "POST /comandas/c1/pagamentos": jsonDoContrato("post", "/comandas/{comandaId}/pagamentos", 201, pagamento()),
      "POST /comandas/c1/fechar": jsonDoContrato(
        "post",
        "/comandas/{comandaId}/fechar",
        200,
        conta({ saldoCentavos: 0, pagoCentavos: 2750, versao: 4, estado: "fechada" }),
      ),
    });

    await digitar(folha, "3000");
    expect(n(folha.querySelector(".fechar-troco-valor")?.textContent)).toBe("R$ 2,50");
    await userEvent.click(within(folha).getByRole("button", { name: "Receber" }));

    expect(await screen.findByText("Conta fechada")).toBeVisible();
    expect(n(document.querySelector(".fechar-troco-valor")?.textContent)).toBe("R$ 2,50");
    const pago = chamadas.find((c) => c.method === "POST" && c.path.endsWith("/comandas/c1/pagamentos"));
    expect(pago!.body).toMatchObject({ recebidoCentavos: 3000, versao: 2 });
    expect(chamadas.some((c) => c.path.endsWith("/comandas/c1/fechar"))).toBe(true);
  });

  it("turns an amount below the balance into a partial payment that leaves the comanda open", async () => {
    const { chamadas, folha } = await abrirFolha({
      "POST /comandas/c1/pagamentos": json(
        pagamento({ recebidoCentavos: 1000, aplicadoCentavos: 1000, trocoCentavos: 0, saldoRestanteCentavos: 1750 }),
        { status: 201 },
      ),
    });

    await digitar(folha, "1000");
    expect(n(within(folha).getByLabelText("Resumo do recebimento").textContent)).toContain("falta R$ 17,50");
    await userEvent.click(within(folha).getByRole("button", { name: "Receber" }));

    expect(await screen.findByText("Recebimento registrado")).toBeVisible();
    expect(n(screen.getByText(/A comanda segue aberta/).textContent)).toContain("R$ 17,50");
    expect(chamadas.some((c) => c.path.endsWith("/comandas/c1/fechar"))).toBe(false);
  });

  it("says to open the cash register when it is closed", async () => {
    const { folha } = await abrirFolha({ "GET /caixa/turnos/atual": json({}, { status: 404 }) });

    expect(await within(folha).findByText("Abra o caixa para receber.")).toBeVisible();
    expect(within(folha).getByRole("link", { name: "Ir para o caixa" })).toHaveAttribute("href", "/salao/caixa");
    expect(within(folha).getByRole("button", { name: "Receber" })).toBeDisabled();
  });

  it("learns the register is closed from the server when the person cannot see it", async () => {
    const { folha } = await abrirFolha(
      {
        "POST /comandas/c1/pagamentos": jsonDoContrato("post", "/comandas/{comandaId}/pagamentos", 409, {
          codigo: "caixa-fechado",
        }),
      },
      CAIXA.filter((p) => p !== "caixa:ver"),
    );

    await userEvent.click(within(folha).getByRole("button", { name: "Receber" }));

    expect(await within(folha).findByText("Abra o caixa para receber.")).toBeVisible();
  });

  it("removes the service fee only for who may, and shows the new balance", async () => {
    let removida = false;
    const { folha } = await abrirFolha(
      {
        "GET /comandas/c1/conta": () =>
          json(
            removida
              ? conta({
                  taxaServicoCentavos: 0,
                  totalCentavos: 2500,
                  saldoCentavos: 2500,
                  taxaServicoRemovida: true,
                  versao: 3,
                })
              : conta(),
          ),
        "POST /comandas/c1/taxa-servico/remover": () => {
          removida = true;
          return json(
            conta({
              taxaServicoCentavos: 0,
              totalCentavos: 2500,
              saldoCentavos: 2500,
              taxaServicoRemovida: true,
              versao: 3,
            }),
          );
        },
      },
      [...CAIXA, "comanda:remover-taxa-servico"],
    );

    await userEvent.click(within(folha).getByRole("button", { name: "Remover serviço" }));

    expect(await within(folha).findByText("Serviço removido")).toBeVisible();
    expect(within(folha).getByRole("button", { name: /^Dinheiro: R\$\s?25,00$/ })).toBeVisible();
  });

  it("does not offer to remove the fee without the permission", async () => {
    const { folha } = await abrirFolha();

    expect(within(folha).queryByRole("button", { name: "Remover serviço" })).toBeNull();
  });

  it("hides Fechar conta from who cannot receive", async () => {
    stubRede(rotas({}, ["comanda:ver", "comanda:lancar-item"]));
    await renderEm("/salao/comanda/c1");
    await screen.findByRole("heading", { level: 2, name: "Ana" });

    expect(screen.queryByRole("button", { name: "Fechar conta" })).toBeNull();
  });

  it("with no signal the payment waits in the queue under its own id", async () => {
    const { folha } = await abrirFolha();
    Object.defineProperty(window.navigator, "onLine", { configurable: true, value: false });
    onlineManager.setOnline(false);

    await userEvent.click(within(folha).getByRole("button", { name: "Receber" }));

    expect(await screen.findByText("Recebimento na fila")).toBeVisible();
    expect(screen.getByText(/ficou na fila/)).toBeVisible();
    const guardada = JSON.parse(window.localStorage.getItem("f4.fila.v1") ?? "[]") as {
      tipo: string;
      recebidoCentavos: number;
      id: string;
    }[];
    expect(guardada).toHaveLength(1);
    expect(guardada[0]).toMatchObject({ tipo: "pagamento", recebidoCentavos: 2750 });
    expect(guardada[0]!.id).toMatch(/^[0-9a-f-]{36}$/);
  });

  it("with a payment of the comanda waiting in the queue the sheet is blocked and never makes a second payment id", async () => {
    const chamadas = stubRede(rotas());
    await renderEm("/salao/comanda/c1");
    await screen.findByRole("heading", { level: 2, name: "Ana" });
    Object.defineProperty(window.navigator, "onLine", { configurable: true, value: false });
    onlineManager.setOnline(false);
    enfileirarPagamento({
      pagamentoId: "pag-1",
      comandaId: "c1",
      recebidoCentavos: 2750,
      titulo: "Recebimento · Ana",
      detalhe: "Dinheiro",
    });

    await userEvent.click(screen.getByRole("button", { name: "Fechar conta" }));
    const folha = await screen.findByRole("dialog", { name: "Fechar conta" });

    expect(within(folha).getByText(/recebimento de R\$\s?27,50 nesta comanda esperando na fila/)).toBeVisible();
    expect(within(folha).getByRole("button", { name: "Receber" })).toBeDisabled();
    expect(lerFila().map((a) => a.id)).toEqual(["pag-1"]);
    expect(chamadas.filter((c) => c.path.endsWith("/pagamentos"))).toHaveLength(0);
  });
});

const COM_METODOS = [
  ...CAIXA,
  "comanda:dividir",
  "pagamento:ver",
  "pagamento:registrar",
  "pagamento:dividir",
  "pagamento:registrar-gorjeta",
];
const metodo = (
  nome: string,
  ordem: number,
  extra: Partial<components["schemas"]["MetodoDePagamentoDto"]> = {},
): components["schemas"]["MetodoDePagamentoDto"] => ({
  id: `m-${nome}`,
  chave: nome.toLowerCase(),
  nome,
  dinheiro: nome === "Dinheiro",
  ativo: true,
  ordem,
  ...extra,
});
const METODOS = [
  metodo("Pix", 2),
  metodo("Dinheiro", 1),
  metodo("Crédito", 3),
  metodo("Vale Posto", 9, { chave: "personalizado" }),
  metodo("Voucher", 4, { ativo: false }),
];
const recebimento = (
  pagamentos: Partial<components["schemas"]["PagamentoDto"]>[],
  contaDepois: Partial<components["schemas"]["ContaDto"]> = {},
): components["schemas"]["RecebimentoDto"] => ({
  pagamentos: pagamentos.map((p, i) => pagamento({ id: `p${i}`, ...p })),
  conta: conta({ saldoCentavos: 0, pagoCentavos: 2750, versao: 3, ...contaDepois }),
});
const metodosDoServidor = { "GET /pagamentos/metodos": jsonDoContrato("get", "/pagamentos/metodos", 200, METODOS) };

async function abrirComMetodos(extra: Record<string, unknown> = {}, permissoes = COM_METODOS) {
  const r = await abrirFolha({ ...metodosDoServidor, ...extra }, permissoes);
  await within(r.folha).findByRole("group", { name: /Método/ });
  return r;
}

const chip = (folha: HTMLElement, nome: string) => within(folha).getByRole("button", { name: nome });
const aoServidor = (chamadas: { method: string; path: string; body: unknown }[], fim: string) =>
  chamadas.filter((c) => c.method === "POST" && c.path.endsWith(fim));

describe("receive with the methods of the organization", () => {
  it("offers the active methods the API sent, in its order, and none of its own", async () => {
    const { folha } = await abrirComMetodos();

    const nomes = within(within(folha).getByRole("group", { name: /Método/ }))
      .getAllByRole("button")
      .map((b) => b.textContent);
    expect(nomes).toEqual(["Dinheiro", "Pix", "Crédito", "Vale Posto"]);
    expect(within(folha).getByRole("button", { name: "Receber" })).toBeDisabled();
    expect(within(folha).getByText("Escolha como receber.")).toBeVisible();
  });

  it("shows a placeholder while the methods load", async () => {
    const { folha } = await abrirFolha(
      { "GET /pagamentos/metodos": () => new Promise<Response>(() => undefined) },
      COM_METODOS,
    );

    expect(await within(folha).findByRole("status", { name: "Carregando métodos de pagamento" })).toBeVisible();
  });

  it("falls back to cash only, in words, when the methods cannot be read", async () => {
    const { folha } = await abrirFolha({ "GET /pagamentos/metodos": new Response(null, { status: 403 }) }, COM_METODOS);

    expect(await within(folha).findByText(/só dinheiro/)).toBeVisible();
    expect(within(folha).getByRole("button", { name: "Receber" })).toBeEnabled();
  });

  it("receives the whole balance with one tap on a method: one line, the id made on the client, no tip", async () => {
    const { chamadas, folha } = await abrirComMetodos({
      "POST /comandas/c1/recebimentos": jsonDoContrato(
        "post",
        "/comandas/{comandaId}/recebimentos",
        201,
        recebimento([
          { metodo: "pix", metodoNome: "Pix", recebidoCentavos: 2750, aplicadoCentavos: 2750, trocoCentavos: 0 },
        ]),
      ),
      "POST /comandas/c1/fechar": jsonDoContrato(
        "post",
        "/comandas/{comandaId}/fechar",
        200,
        conta({ saldoCentavos: 0, pagoCentavos: 2750, versao: 4, estado: "fechada" }),
      ),
    });

    await userEvent.click(chip(folha, "Pix"));
    expect(within(folha).getByRole("button", { name: /^Pix: R\$\s?27,50$/ })).toBeVisible();
    await userEvent.click(within(folha).getByRole("button", { name: "Receber" }));

    expect(await screen.findByText("Conta fechada")).toBeVisible();
    const [chamada] = aoServidor(chamadas, "/comandas/c1/recebimentos");
    expect(chamada!.body).toMatchObject({
      versao: 2,
      linhas: [
        { metodoId: "m-Pix", recebidoCentavos: 2750, gorjetaCentavos: null, divisaoId: null, divisaoParte: null },
      ],
    });
    expect((chamada!.body as { linhas: { pagamentoId: string }[] }).linhas[0]!.pagamentoId).toMatch(/^[0-9a-f-]{36}$/);
    expect(aoServidor(chamadas, "/comandas/c1/pagamentos")).toHaveLength(0);
  });

  it("mixes two methods: the live balance follows the keypad, and one request carries both lines", async () => {
    const { chamadas, folha } = await abrirComMetodos({
      "POST /comandas/c1/recebimentos": jsonDoContrato(
        "post",
        "/comandas/{comandaId}/recebimentos",
        201,
        recebimento([{ aplicadoCentavos: 1750 }, { aplicadoCentavos: 1000 }], { saldoCentavos: 0 }),
      ),
      "POST /comandas/c1/fechar": jsonDoContrato(
        "post",
        "/comandas/{comandaId}/fechar",
        200,
        conta({ saldoCentavos: 0, versao: 4, estado: "fechada" }),
      ),
    });

    await userEvent.click(chip(folha, "Pix"));
    await userEvent.click(chip(folha, "Dinheiro"));
    await digitar(folha, "1000");

    expect(within(folha).getByRole("button", { name: /^Pix: R\$\s?17,50$/ })).toBeVisible();
    expect(within(folha).getByRole("button", { name: /^Dinheiro: R\$\s?10,00$/ })).toBeVisible();
    expect(n(within(folha).getByLabelText("Resumo do recebimento").textContent)).toContain("falta R$ 0,00");
    await userEvent.click(within(folha).getByRole("button", { name: "Receber" }));

    await screen.findByText("Conta fechada");
    const [chamada] = aoServidor(chamadas, "/comandas/c1/recebimentos");
    const linhas = (chamada!.body as { linhas: { metodoId: string; recebidoCentavos: number }[] }).linhas;
    expect(linhas.map((l) => [l.metodoId, l.recebidoCentavos])).toEqual([
      ["m-Pix", 1750],
      ["m-Dinheiro", 1000],
    ]);
  });

  it("measures a mixed payment of two methods in at most 8 taps (open, 2 methods, 4 digits, receive)", async () => {
    const { folha } = await abrirComMetodos({
      "POST /comandas/c1/recebimentos": jsonDoContrato(
        "post",
        "/comandas/{comandaId}/recebimentos",
        201,
        recebimento([{ aplicadoCentavos: 1750 }, { aplicadoCentavos: 1000 }], { saldoCentavos: 0 }),
      ),
      "POST /comandas/c1/fechar": jsonDoContrato(
        "post",
        "/comandas/{comandaId}/fechar",
        200,
        conta({ saldoCentavos: 0, versao: 4, estado: "fechada" }),
      ),
    });
    let toques = 1; // "Fechar conta" opened the sheet
    for (const nome of ["Pix", "Dinheiro"]) {
      await userEvent.click(chip(folha, nome));
      toques += 1;
    }

    await digitar(folha, "1000");
    toques += 4;
    await userEvent.click(within(folha).getByRole("button", { name: "Receber" }));
    toques += 1;

    expect(toques).toBeLessThanOrEqual(8);
    expect(await screen.findByText("Conta fechada")).toBeVisible();
  });

  it("shows the change in large type only for cash", async () => {
    const { folha } = await abrirComMetodos();

    await userEvent.click(chip(folha, "Dinheiro"));
    await digitar(folha, "3000");
    expect(n(folha.querySelector(".fechar-troco-valor")?.textContent)).toBe("R$ 2,50");

    await userEvent.click(chip(folha, "Dinheiro"));
    await userEvent.click(chip(folha, "Pix"));
    expect(folha.querySelector(".fechar-troco-valor")).toBeNull();
  });

  it("blocks methods that are not cash passing the balance, in words, and never sends them", async () => {
    const { chamadas, folha } = await abrirComMetodos();

    await userEvent.click(chip(folha, "Pix"));
    await digitar(folha, "2000");
    await userEvent.click(chip(folha, "Crédito"));
    await digitar(folha, "1000");

    expect(within(folha).getByText(/passam do que falta/)).toBeVisible();
    expect(within(folha).getByRole("button", { name: "Receber" })).toBeDisabled();
    expect(aoServidor(chamadas, "/recebimentos")).toHaveLength(0);
  });

  it("a partial payment leaves the comanda open with what remains", async () => {
    const { chamadas, folha } = await abrirComMetodos({
      "POST /comandas/c1/recebimentos": jsonDoContrato(
        "post",
        "/comandas/{comandaId}/recebimentos",
        201,
        recebimento([{ aplicadoCentavos: 1000, trocoCentavos: 0 }], { saldoCentavos: 1750, pagoCentavos: 1000 }),
      ),
    });

    await userEvent.click(chip(folha, "Pix"));
    await digitar(folha, "1000");
    expect(n(within(folha).getByLabelText("Resumo do recebimento").textContent)).toContain("falta R$ 17,50");
    await userEvent.click(within(folha).getByRole("button", { name: "Receber" }));

    expect(await screen.findByText("Recebimento registrado")).toBeVisible();
    expect(n(screen.getByText(/A comanda segue aberta/).textContent)).toContain("R$ 17,50");
    expect(chamadas.some((c) => c.path.endsWith("/fechar"))).toBe(false);
  });

  it("without pagamento:dividir a second method replaces the first (one line only)", async () => {
    const { folha } = await abrirComMetodos(
      {},
      COM_METODOS.filter((p) => p !== "pagamento:dividir"),
    );

    await userEvent.click(chip(folha, "Pix"));
    await userEvent.click(chip(folha, "Crédito"));

    expect(within(folha).getAllByRole("button", { name: /^(Pix|Crédito): R\$/ })).toHaveLength(1);
    expect(within(folha).getByRole("button", { name: /^Crédito: R\$/ })).toBeVisible();
    expect(within(folha).getByText("Método")).toBeVisible();
  });

  it("takes a tip only with pagamento:registrar-gorjeta, on the first line, outside the balance", async () => {
    const { chamadas, folha } = await abrirComMetodos({
      "POST /comandas/c1/recebimentos": jsonDoContrato(
        "post",
        "/comandas/{comandaId}/recebimentos",
        201,
        recebimento([{ gorjetaCentavos: 300 }], { saldoCentavos: 0 }),
      ),
      "POST /comandas/c1/fechar": jsonDoContrato(
        "post",
        "/comandas/{comandaId}/fechar",
        200,
        conta({ saldoCentavos: 0, versao: 4, estado: "fechada" }),
      ),
    });

    await userEvent.click(chip(folha, "Pix"));
    await userEvent.click(within(folha).getByRole("button", { name: /^Gorjeta:/ }));
    await digitar(folha, "300");
    expect(n(within(folha).getByLabelText("Resumo do recebimento").textContent)).toContain("gorjeta R$ 3,00");
    expect(n(within(folha).getByLabelText("Resumo do recebimento").textContent)).toContain("falta R$ 0,00");
    await userEvent.click(within(folha).getByRole("button", { name: "Receber" }));

    await screen.findByText("Conta fechada");
    const [chamada] = aoServidor(chamadas, "/recebimentos");
    expect((chamada!.body as { linhas: { gorjetaCentavos: number }[] }).linhas[0]!.gorjetaCentavos).toBe(300);
  });

  it("hides the tip without the permission", async () => {
    const { folha } = await abrirComMetodos(
      {},
      COM_METODOS.filter((p) => p !== "pagamento:registrar-gorjeta"),
    );

    await userEvent.click(chip(folha, "Pix"));

    expect(within(folha).queryByRole("button", { name: /^Gorjeta:/ })).toBeNull();
  });

  it("a tip on cash goes through the new route, never the old one", async () => {
    const { chamadas, folha } = await abrirComMetodos({
      "POST /comandas/c1/recebimentos": jsonDoContrato(
        "post",
        "/comandas/{comandaId}/recebimentos",
        201,
        recebimento([{ gorjetaCentavos: 200 }]),
      ),
      "POST /comandas/c1/fechar": jsonDoContrato(
        "post",
        "/comandas/{comandaId}/fechar",
        200,
        conta({ saldoCentavos: 0 }),
      ),
    });

    await userEvent.click(chip(folha, "Dinheiro"));
    await userEvent.click(within(folha).getByRole("button", { name: /^Gorjeta:/ }));
    await digitar(folha, "200");
    await userEvent.click(within(folha).getByRole("button", { name: "Receber" }));

    await screen.findByText("Conta fechada");
    expect(aoServidor(chamadas, "/recebimentos")).toHaveLength(1);
    expect(aoServidor(chamadas, "/pagamentos")).toHaveLength(0);
  });
});

describe("receive the part of a split", () => {
  const parte = (numero: number, total: number): components["schemas"]["ParteDaDivisaoDto"] => ({
    numero,
    assento: null,
    itemIds: [],
    consumoCentavos: total,
    taxaServicoCentavos: 0,
    totalCentavos: total,
    gorjetaCentavos: 0,
    aPagarCentavos: total,
  });
  const divisao = (vigente = true): components["schemas"]["DivisaoDto"] => ({
    id: "d1",
    comandaId: "c1",
    modo: "iguais",
    comandaVersao: 2,
    vigente,
    totalCentavos: 2750,
    taxaServicoCentavos: 250,
    gorjetaCentavos: 0,
    criadoEm: new Date().toISOString(),
    partes: [parte(1, 1375), parte(2, 1375)],
  });
  const comDivisao = (pagas: components["schemas"]["PagamentoDto"][] = []) => ({
    ...metodosDoServidor,
    "GET /comandas/c1/divisao": jsonDoContrato("get", "/comandas/{comandaId}/divisao", 200, divisao()),
    "GET /comandas/c1/pagamentos": jsonDoContrato("get", "/comandas/{comandaId}/pagamentos", 200, pagas),
  });

  it("shows whose part is being paid and what it still owes, starting at the first part not paid", async () => {
    const paga = pagamento({ divisaoId: "d1", divisaoParte: 1, aplicadoCentavos: 1375, saldoRestanteCentavos: 1375 });
    const { folha } = await abrirComMetodos(comDivisao([paga]), COM_METODOS);

    const grupo = await within(folha).findByRole("group", { name: "Quem paga" });
    expect(within(grupo).getByRole("button", { name: /Parte 1 · paga/ })).toBeDisabled();
    expect(within(grupo).getByRole("button", { name: /Parte 2 · R\$\s?13,75/ })).toHaveAttribute(
      "aria-pressed",
      "true",
    );
    expect(n(within(folha).getByLabelText("Valor a receber").textContent)).toContain("Parte 2");
    expect(n(within(folha).getByLabelText("Valor a receber").textContent)).toContain("R$ 13,75");
  });

  it("sends the split id and the part number on every line, and leaves the bill open with what remains", async () => {
    const { chamadas, folha } = await abrirComMetodos(
      {
        ...comDivisao(),
        "POST /comandas/c1/recebimentos": jsonDoContrato(
          "post",
          "/comandas/{comandaId}/recebimentos",
          201,
          recebimento([{ divisaoId: "d1", divisaoParte: 1 }], { saldoCentavos: 1375, pagoCentavos: 1375 }),
        ),
      },
      COM_METODOS,
    );

    await within(folha).findByRole("group", { name: "Quem paga" });
    await userEvent.click(chip(folha, "Pix"));
    expect(within(folha).getByRole("button", { name: /^Pix: R\$\s?13,75$/ })).toBeVisible();
    await userEvent.click(within(folha).getByRole("button", { name: "Receber" }));

    expect(await screen.findByText("Recebimento registrado")).toBeVisible();
    expect(screen.getByText(/Parte 1 recebida/)).toBeVisible();
    const [chamada] = aoServidor(chamadas, "/recebimentos");
    expect(chamada!.body).toMatchObject({
      linhas: [{ recebidoCentavos: 1375, divisaoId: "d1", divisaoParte: 1 }],
    });
  });

  it("lets the cashier receive the whole bill instead of a part", async () => {
    const { chamadas, folha } = await abrirComMetodos(
      {
        ...comDivisao(),
        "POST /comandas/c1/recebimentos": jsonDoContrato(
          "post",
          "/comandas/{comandaId}/recebimentos",
          201,
          recebimento([{}]),
        ),
        "POST /comandas/c1/fechar": jsonDoContrato(
          "post",
          "/comandas/{comandaId}/fechar",
          200,
          conta({ saldoCentavos: 0 }),
        ),
      },
      COM_METODOS,
    );

    await userEvent.click(await within(folha).findByRole("button", { name: "Conta toda" }));
    await userEvent.click(chip(folha, "Pix"));
    await userEvent.click(within(folha).getByRole("button", { name: "Receber" }));

    await screen.findByText("Conta fechada");
    expect(aoServidor(chamadas, "/recebimentos")[0]!.body).toMatchObject({
      linhas: [{ recebidoCentavos: 2750, divisaoId: null, divisaoParte: null }],
    });
  });

  it("offers no part when the split is no longer current", async () => {
    const { folha } = await abrirComMetodos(
      {
        ...metodosDoServidor,
        "GET /comandas/c1/divisao": jsonDoContrato("get", "/comandas/{comandaId}/divisao", 200, divisao(false)),
      },
      COM_METODOS,
    );

    expect(within(folha).queryByRole("group", { name: "Quem paga" })).toBeNull();
  });
});

describe("what goes wrong while receiving with methods", () => {
  async function pagarPix(extra: Record<string, unknown>) {
    const r = await abrirComMetodos(extra);
    await userEvent.click(chip(r.folha, "Pix"));
    await userEvent.click(within(r.folha).getByRole("button", { name: "Receber" }));
    return r;
  }

  it("on a 409 of version it refreshes the numbers, keeps the lines and says so", async () => {
    let lidas = 0;
    const { chamadas, folha } = await abrirComMetodos({
      "GET /comandas/c1/conta": () => {
        lidas += 1;
        return json(lidas > 1 ? conta({ versao: 5 }) : conta());
      },
      "POST /comandas/c1/recebimentos": jsonDoContrato("post", "/comandas/{comandaId}/recebimentos", 409, {
        codigo: "versao-desatualizada",
        versaoAtual: 5,
      }),
    });
    await userEvent.click(chip(folha, "Pix"));
    await userEvent.click(within(folha).getByRole("button", { name: "Receber" }));

    expect(await within(folha).findByText(/alterada em outro aparelho/)).toBeVisible();
    expect(within(folha).getByRole("button", { name: /^Pix: R\$/ })).toBeVisible();
    expect(lidas).toBeGreaterThan(1);
    // The next tap carries the version the server said, with the same payment id (a repeat, not a second payment).
    await userEvent.click(within(folha).getByRole("button", { name: "Receber" }));
    const envios = aoServidor(chamadas, "/recebimentos").map(
      (c) => c.body as { versao: number; linhas: { pagamentoId: string }[] },
    );
    expect(envios).toHaveLength(2);
    expect(envios[1]!.versao).toBe(5);
    expect(envios[1]!.linhas[0]!.pagamentoId).toBe(envios[0]!.linhas[0]!.pagamentoId);
  });

  it("on a 403 says only that the person may not, with no limit and no reason", async () => {
    const { folha } = await pagarPix({
      "POST /comandas/c1/recebimentos": new Response(null, { status: 403 }),
    });

    const aviso = await within(folha).findByText("Você não tem permissão para receber desta forma.");
    expect(aviso).toBeVisible();
    expect(aviso.closest(".f4-sheet-footer")).not.toBeNull();
  });

  it("on a closed cash register says to open it", async () => {
    const { folha } = await pagarPix({
      "POST /comandas/c1/recebimentos": jsonDoContrato("post", "/comandas/{comandaId}/recebimentos", 409, {
        codigo: "caixa-fechado",
      }),
    });

    expect(await within(folha).findByText("Abra o caixa para receber.")).toBeVisible();
  });

  it("on a refused part says what to do and keeps the lines", async () => {
    const { folha } = await pagarPix({
      "POST /comandas/c1/recebimentos": jsonDoContrato("post", "/comandas/{comandaId}/recebimentos", 409, {
        codigo: "excede-saldo",
      }),
    });

    expect(await within(folha).findByText(/passam do que falta na conta/)).toBeVisible();
    expect(within(folha).getByRole("button", { name: /^Pix:/ })).toBeVisible();
  });

  it("on a lost answer says to tap again: the same id makes it a repeat", async () => {
    const { folha } = await pagarPix({
      "POST /comandas/c1/recebimentos": () => {
        throw new TypeError("Failed to fetch");
      },
    });

    expect(await within(folha).findByText(/nada é cobrado duas vezes/)).toBeVisible();
  });
});

describe("without a signal", () => {
  it("a method other than cash needs the server: the reason is in the footer, the button is off and nothing is queued", async () => {
    const { folha } = await abrirComMetodos();
    await userEvent.click(chip(folha, "Pix"));
    Object.defineProperty(window.navigator, "onLine", { configurable: true, value: false });
    onlineManager.setOnline(false);
    window.dispatchEvent(new Event("offline"));

    const motivo = await within(folha).findByText(/precisa do servidor e não fica na fila/);
    expect(motivo.closest(".f4-sheet-footer")).not.toBeNull();
    expect(within(folha).getByRole("button", { name: "Receber" })).toBeDisabled();
    expect(lerFila()).toHaveLength(0);
  });

  it("one line of cash keeps waiting in the queue, as before", async () => {
    const { folha } = await abrirComMetodos();
    await userEvent.click(chip(folha, "Dinheiro"));
    Object.defineProperty(window.navigator, "onLine", { configurable: true, value: false });
    onlineManager.setOnline(false);
    window.dispatchEvent(new Event("offline"));

    await userEvent.click(within(folha).getByRole("button", { name: "Receber" }));

    expect(await screen.findByText("Recebimento na fila")).toBeVisible();
    expect(lerFila()).toHaveLength(1);
  });
});
