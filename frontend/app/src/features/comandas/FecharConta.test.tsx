import { onlineManager } from "@tanstack/react-query";
import { screen, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, describe, expect, it, vi } from "vitest";
import { json, renderEm, sessaoJson, stubRede } from "../../test-utils";
import { reiniciarFilaParaTestes } from "../fila/fila";

afterEach(() => {
  vi.unstubAllGlobals();
  Object.defineProperty(window.navigator, "onLine", { configurable: true, value: true });
  onlineManager.setOnline(true);
  reiniciarFilaParaTestes();
});

const CAIXA = ["comanda:abrir", "comanda:ver", "comanda:fechar", "comanda:receber-pagamento", "caixa:ver", "mesa:ver", "produto:ver"];
const comandaJson = { id: "c1", unidadeId: "u1", numero: 7, numeroTexto: "#7", identificacao: "Ana", mesaId: null, canal: "salao", garcomId: "g1", pessoas: null, estado: "aberta", taxaServicoPercentual: 10, taxaServicoRemovida: false, versao: 2, abertaEm: new Date().toISOString() };
const conta = (extra: Record<string, unknown> = {}) => ({ consumoCentavos: 2500, taxaServicoCentavos: 250, totalCentavos: 2750, pagoCentavos: 0, saldoCentavos: 2750, taxaServicoRemovida: false, taxaServicoPercentual: 10, versao: 2, estado: "aberta", ...extra });
const pagamento = (extra: Record<string, unknown> = {}) => ({ id: "p", comandaId: "c1", turnoId: "t", metodo: "dinheiro", recebidoCentavos: 3000, aplicadoCentavos: 2750, trocoCentavos: 250, saldoRestanteCentavos: 0, comandaVersao: 3, registradoEm: new Date().toISOString(), operadorId: "o", ...extra });

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

describe("close the bill in cash", () => {
  it("shows consumption, fee, total, the amount to receive and only the Dinheiro method", async () => {
    const { folha } = await abrirFolha();

    const conta_ = within(folha).getByLabelText("Conta");
    expect(conta_.textContent).toContain("Consumo");
    expect(conta_.textContent).toContain("Serviço (10%)");
    expect(n(conta_.textContent)).toContain("R$ 27,50");
    expect(n(within(folha).getByLabelText("Valor a receber").textContent)).toContain("R$ 27,50");
    const botoes = within(folha).getAllByRole("button").map((b) => b.textContent);
    expect(botoes).toEqual(["Voltar", "Dinheiro"]);
    expect(folha.textContent).not.toMatch(/Pix|Crédito|Débito|Voucher|Dividir/i);
  });

  it("shows the change in large type and pays, then closes the bill", async () => {
    const { chamadas, folha } = await abrirFolha({
      "POST /comandas/c1/pagamentos": json(pagamento(), { status: 201 }),
      "POST /comandas/c1/fechar": json(conta({ saldoCentavos: 0, pagoCentavos: 2750, versao: 4, estado: "fechada" })),
    });

    await userEvent.clear(within(folha).getByLabelText("Dinheiro recebido"));
    await userEvent.type(within(folha).getByLabelText("Dinheiro recebido"), "3000");
    expect(n(folha.querySelector(".fechar-troco-valor")?.textContent)).toBe("R$ 2,50");
    await userEvent.click(within(folha).getByRole("button", { name: "Dinheiro" }));

    expect(await screen.findByText("Conta fechada")).toBeVisible();
    expect(n(document.querySelector(".fechar-troco-valor")?.textContent)).toBe("R$ 2,50");
    const pago = chamadas.find((c) => c.method === "POST" && c.path.endsWith("/comandas/c1/pagamentos"));
    expect(pago!.body).toMatchObject({ recebidoCentavos: 3000, versao: 2 });
    expect(chamadas.some((c) => c.path.endsWith("/comandas/c1/fechar"))).toBe(true);
  });

  it("turns an amount below the balance into a partial payment that leaves the comanda open", async () => {
    const { chamadas, folha } = await abrirFolha({
      "POST /comandas/c1/pagamentos": json(pagamento({ recebidoCentavos: 1000, aplicadoCentavos: 1000, trocoCentavos: 0, saldoRestanteCentavos: 1750 }), { status: 201 }),
    });

    await userEvent.clear(within(folha).getByLabelText("Dinheiro recebido"));
    await userEvent.type(within(folha).getByLabelText("Dinheiro recebido"), "1000");
    expect(within(folha).getByText(/Pagamento parcial/)).toBeVisible();
    expect(n(within(folha).getByLabelText("Valor a receber").textContent)).toContain("restará R$ 17,50");
    await userEvent.click(within(folha).getByRole("button", { name: "Dinheiro" }));

    expect(await screen.findByText("Recebimento registrado")).toBeVisible();
    expect(n(screen.getByText(/A comanda segue aberta/).textContent)).toContain("R$ 17,50");
    expect(chamadas.some((c) => c.path.endsWith("/comandas/c1/fechar"))).toBe(false);
  });

  it("says to open the cash register when it is closed", async () => {
    const { folha } = await abrirFolha({ "GET /caixa/turnos/atual": json({}, { status: 404 }) });

    expect(await within(folha).findByText("Abra o caixa para receber.")).toBeVisible();
    expect(within(folha).getByRole("link", { name: "Ir para o caixa" })).toHaveAttribute("href", "/salao/caixa");
    expect(within(folha).getByRole("button", { name: "Dinheiro" })).toBeDisabled();
  });

  it("learns the register is closed from the server when the person cannot see it", async () => {
    const { folha } = await abrirFolha({ "POST /comandas/c1/pagamentos": json({ codigo: "caixa-fechado" }, { status: 409 }) }, CAIXA.filter((p) => p !== "caixa:ver"));

    await userEvent.click(within(folha).getByRole("button", { name: "Dinheiro" }));

    expect(await within(folha).findByText("Abra o caixa para receber.")).toBeVisible();
  });

  it("removes the service fee only for who may, and shows the new balance", async () => {
    let removida = false;
    const { folha } = await abrirFolha(
      {
        "GET /comandas/c1/conta": () => json(removida ? conta({ taxaServicoCentavos: 0, totalCentavos: 2500, saldoCentavos: 2500, taxaServicoRemovida: true, versao: 3 }) : conta()),
        "POST /comandas/c1/taxa-servico/remover": () => {
          removida = true;
          return json(conta({ taxaServicoCentavos: 0, totalCentavos: 2500, saldoCentavos: 2500, taxaServicoRemovida: true, versao: 3 }));
        },
      },
      [...CAIXA, "comanda:remover-taxa-servico"],
    );

    await userEvent.click(within(folha).getByRole("button", { name: "Remover serviço" }));

    expect(await within(folha).findByText("Serviço removido")).toBeVisible();
    expect(n((within(folha).getByLabelText("Dinheiro recebido") as HTMLInputElement).value)).toBe("R$ 25,00");
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

    await userEvent.click(within(folha).getByRole("button", { name: "Dinheiro" }));

    expect(await screen.findByText("Recebimento na fila")).toBeVisible();
    expect(screen.getByText(/ficou na fila/)).toBeVisible();
    const guardada = JSON.parse(window.localStorage.getItem("f4.fila.v1") ?? "[]") as { tipo: string; recebidoCentavos: number; id: string }[];
    expect(guardada).toHaveLength(1);
    expect(guardada[0]).toMatchObject({ tipo: "pagamento", recebidoCentavos: 2750 });
    expect(guardada[0]!.id).toMatch(/^[0-9a-f-]{36}$/);
  });
});
