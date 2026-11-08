import { screen, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, describe, expect, it, vi } from "vitest";
import { json, renderEm, sessaoJson, stubRede } from "../../test-utils";
import { enfileirarPagamento, enfileirarRodada, lerFila } from "./fila";

afterEach(() => {
  vi.unstubAllGlobals();
  Object.defineProperty(window.navigator, "onLine", { configurable: true, value: true });
});

const pronta = {
  "POST /refresh": json(sessaoJson()),
  "GET /comandas": json([]),
  "GET /caixa/turnos/atual": new Response(null, { status: 404 }),
};

async function comFilaOffline() {
  stubRede(pronta);
  await renderEm("/salao");
  await screen.findByRole("heading", { level: 1, name: "Salão" });
  Object.defineProperty(window.navigator, "onLine", { configurable: true, value: false });
}

describe("removing from the visible queue", () => {
  it("asks before dropping a queued payment, and keeps it when the person declines", async () => {
    await comFilaOffline();
    enfileirarPagamento({
      pagamentoId: "p1",
      comandaId: "c1",
      recebidoCentavos: 2750,
      titulo: "Recebimento · Ana",
      detalhe: "Dinheiro R$ 27,50",
    });
    await userEvent.click(await screen.findByRole("button", { name: "1 ação pendente" }));

    await userEvent.click(screen.getByRole("button", { name: "Remover Recebimento · Ana" }));

    expect(screen.getByText("Remover este recebimento?")).toBeVisible();
    expect(lerFila()).toHaveLength(1);
    await userEvent.click(screen.getByRole("button", { name: "Manter na fila" }));
    expect(lerFila()).toHaveLength(1);
    await userEvent.click(screen.getByRole("button", { name: "Remover Recebimento · Ana" }));
    await userEvent.click(screen.getByRole("button", { name: "Remover recebimento" }));
    expect(lerFila()).toHaveLength(0);
  });

  it("removes a queued round without asking", async () => {
    await comFilaOffline();
    enfileirarRodada({
      rodadaId: "r1",
      comandaId: "c1",
      itens: [{ produtoId: "p", quantidade: 1, observacao: null }],
      titulo: "Rodada · #7",
      detalhe: "1 item",
    });
    await userEvent.click(await screen.findByRole("button", { name: "1 ação pendente" }));

    await userEvent.click(
      within(screen.getByRole("list", { name: "Ações pendentes" })).getByRole("button", {
        name: "Remover Rodada · #7",
      }),
    );

    expect(lerFila()).toHaveLength(0);
  });
});
