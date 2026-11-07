import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { json, sessaoJson, stubRede, tokenDe } from "../../test-utils";
import { entrar, reiniciarSessaoParaTestes } from "../sessao/sessao";
import {
  enfileirarPagamento,
  enfileirarRodada,
  iniciarProcessador,
  lerFila,
  limparFila,
  processar,
  recarregarFilaParaTestes,
  reiniciarFilaParaTestes,
  removerAcao,
  tentarAgora,
} from "./fila";

const contaOk = (versao = 4) =>
  json({
    consumoCentavos: 0,
    taxaServicoCentavos: 0,
    totalCentavos: 0,
    pagoCentavos: 0,
    saldoCentavos: 0,
    versao,
    estado: "aberta",
    taxaServicoRemovida: false,
    taxaServicoPercentual: 10,
  });
const rodadaOk = (id: string) =>
  json(
    { id, comandaId: "c1", enviadaEm: new Date().toISOString(), desfeitaEm: null, comandaVersao: 5, itens: [] },
    { status: 201 },
  );
const itens = [{ produtoId: "p1", quantidade: 2, observacao: null }];

async function entrarComo(conta = "conta-1") {
  reiniciarSessaoParaTestes();
  stubRede({ "POST /login": json(sessaoJson({ accessToken: tokenDe(conta) })) });
  await entrar({ email: "a@b.test", senha: "x" });
}

function enfileirar(id: string, comandaId = "c1") {
  enfileirarRodada({ rodadaId: id, comandaId, itens, titulo: "Rodada · #7", detalhe: "2 itens · R$ 25,00" });
}

function online(valor: boolean) {
  Object.defineProperty(window.navigator, "onLine", { configurable: true, value: valor });
}

beforeEach(async () => {
  reiniciarFilaParaTestes();
  online(true);
  await entrarComo();
});

afterEach(() => {
  vi.unstubAllGlobals();
  vi.useRealTimers();
  online(true);
});

describe("pending-actions queue", () => {
  it("keeps an action waiting while there is no signal and sends it when the signal returns", async () => {
    const chamadas = stubRede({ "GET /comandas/c1/conta": contaOk(), "POST /comandas/c1/rodadas": rodadaOk("r1") });
    online(false);

    enfileirar("r1");
    await processar();

    expect(lerFila().map((a) => [a.id, a.estado])).toEqual([["r1", "pendente"]]);
    expect(chamadas).toHaveLength(0);

    online(true);
    await processar();

    expect(lerFila()).toEqual([]);
    expect(chamadas.map((c) => `${c.method} ${c.path}`)).toEqual([
      "GET /comandas/c1/conta",
      "POST /comandas/c1/rodadas",
    ]);
  });

  it("sends the round under the id the client made, with the version read at send time", async () => {
    const chamadas = stubRede({ "GET /comandas/c1/conta": contaOk(9), "POST /comandas/c1/rodadas": rodadaOk("r1") });

    enfileirar("0197e2e0-0000-7000-8000-000000000042");
    await vi.waitFor(() => expect(chamadas.some((c) => c.method === "POST")).toBe(true));

    const post = chamadas.find((c) => c.method === "POST")!;
    expect(post.body).toEqual({ rodadaId: "0197e2e0-0000-7000-8000-000000000042", versao: 9, itens });
  });

  it("sends in the order the rounds were made, and a waiting one holds the ones behind it", async () => {
    const ordem: string[] = [];
    stubRede({
      "GET /comandas/c1/conta": contaOk(),
      "POST /comandas/c1/rodadas": (c) => {
        const id = (c.body as { rodadaId: string }).rodadaId;
        ordem.push(id);
        return id === "r1" && ordem.filter((x) => x === "r1").length === 1
          ? new Response(null, { status: 503 })
          : rodadaOk(id);
      },
    });

    enfileirar("r1");
    enfileirar("r2");
    await vi.waitFor(() => expect(lerFila().find((a) => a.id === "r1")?.tentativas).toBe(1));

    // r1 failed with a server error and waits its back-off: r2 does not jump the line.
    expect(ordem).toEqual(["r1"]);
    expect(lerFila().map((a) => [a.id, a.estado])).toEqual([
      ["r1", "pendente"],
      ["r2", "pendente"],
    ]);
    tentarAgora("r1");
    await vi.waitFor(() => expect(lerFila()).toEqual([]));
    expect(ordem).toEqual(["r1", "r1", "r2"]);
  });

  it("retries with a growing back-off after a server error and then goes through", async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    let falhas = 2;
    stubRede({
      "GET /comandas/c1/conta": contaOk(),
      "POST /comandas/c1/rodadas": () => (falhas-- > 0 ? new Response(null, { status: 500 }) : rodadaOk("r1")),
    });

    enfileirar("r1");
    await vi.waitFor(() => expect(lerFila()[0]?.tentativas).toBe(1));
    const primeira = lerFila()[0]!;
    expect(primeira.estado).toBe("pendente");
    expect(primeira.proxima - Date.now()).toBeGreaterThan(1500);
    expect(primeira.proxima - Date.now()).toBeLessThanOrEqual(2000);

    await vi.advanceTimersByTimeAsync(2100);
    await vi.waitFor(() => expect(lerFila()[0]?.tentativas).toBe(2));
    expect(lerFila()[0]!.proxima - Date.now()).toBeGreaterThan(3500);

    await vi.advanceTimersByTimeAsync(4100);
    await vi.waitFor(() => expect(lerFila()).toEqual([]));
  });

  it("treats a lost answer as a repeat: the same id goes again and the server keeps one round", async () => {
    let chamadasDePost = 0;
    const ids: string[] = [];
    stubRede({
      "GET /comandas/c1/conta": contaOk(),
      "POST /comandas/c1/rodadas": (c) => {
        chamadasDePost += 1;
        ids.push((c.body as { rodadaId: string }).rodadaId);
        // The server stored it the first time; the answer never arrived. The repeat is answered 200 with the stored round.
        return chamadasDePost === 1
          ? (Promise.reject(new TypeError("network")) as never)
          : json({
              id: "r1",
              comandaId: "c1",
              enviadaEm: "2026-11-06T20:00:00Z",
              desfeitaEm: null,
              comandaVersao: 5,
              itens: [],
            });
      },
    });

    enfileirar("r1");
    await vi.waitFor(() => expect(lerFila()[0]?.tentativas).toBe(1));
    tentarAgora("r1");
    await vi.waitFor(() => expect(lerFila()).toEqual([]));

    expect(ids).toEqual(["r1", "r1"]);
  });

  it("adopts the version the server names on a conflict, sends the same round and tells the waiter without blocking", async () => {
    let tentativa = 0;
    const versoes: number[] = [];
    stubRede({
      "GET /comandas/c1/conta": contaOk(4),
      "POST /comandas/c1/rodadas": (c) => {
        versoes.push((c.body as { versao: number }).versao);
        tentativa += 1;
        return tentativa === 1
          ? json({ codigo: "versao-desatualizada", versaoAtual: 7 }, { status: 409 })
          : rodadaOk("r1");
      },
    });

    enfileirar("r1");
    await vi.waitFor(() => expect(lerFila()).toEqual([]));

    expect(versoes).toEqual([4, 7]);
  });

  it.each([
    [{ status: 409, corpo: { codigo: "produto-inativo" } }, "Um item não está mais disponível."],
    [{ status: 409, corpo: { codigo: "comanda-nao-aberta" } }, "A comanda não está mais aberta."],
    [{ status: 403, corpo: null }, "Você não tem permissão para lançar itens."],
    [{ status: 400, corpo: { codigo: "invalido" } }, "A rodada não foi aceita. Confira os itens."],
  ])("shows an action the server will never accept as failed, without retrying it: %o", async (resposta, motivo) => {
    const chamadas = stubRede({
      "GET /comandas/c1/conta": contaOk(),
      "POST /comandas/c1/rodadas": resposta.corpo
        ? json(resposta.corpo, { status: resposta.status })
        : new Response(null, { status: resposta.status }),
    });

    enfileirar("r1");
    await vi.waitFor(() => expect(lerFila()[0]?.estado).toBe("falhou"));

    expect(lerFila()[0]?.motivo).toBe(motivo);
    expect(chamadas.filter((c) => c.method === "POST")).toHaveLength(1);
    await processar();
    expect(chamadas.filter((c) => c.method === "POST")).toHaveLength(1);
  });

  it("does not let a failed action block the ones behind it", async () => {
    stubRede({
      "GET /comandas/c1/conta": contaOk(),
      "POST /comandas/c1/rodadas": (c) =>
        (c.body as { rodadaId: string }).rodadaId === "ruim"
          ? json({ codigo: "produto-inativo" }, { status: 409 })
          : rodadaOk("bom"),
    });

    enfileirar("ruim");
    enfileirar("bom");
    await vi.waitFor(() => expect(lerFila().map((a) => [a.id, a.estado])).toEqual([["ruim", "falhou"]]));
  });

  it("stops on a refused session and keeps the actions for when the person signs in again", async () => {
    stubRede({
      "GET /comandas/c1/conta": new Response(null, { status: 401 }),
      "POST /refresh": new Response(null, { status: 401 }),
    });

    enfileirar("r1");
    await vi.waitFor(() => expect(lerFila()[0]?.motivo).toBe("Entre de novo para enviar"));

    expect(lerFila()).toHaveLength(1);
    expect(lerFila()[0]?.estado).toBe("pendente");
  });

  it("never sends the actions of another account", async () => {
    const chamadas = stubRede({ "GET /comandas/c1/conta": contaOk(), "POST /comandas/c1/rodadas": rodadaOk("r1") });
    enfileirar("r1");
    online(false);
    await processar();
    online(true);

    await entrarComo("conta-2");
    await processar();

    expect(chamadas.filter((c) => c.method === "POST")).toHaveLength(0);
    expect(lerFila()).toHaveLength(1);
  });

  it("survives a reload and forgets everything on sign-out", async () => {
    online(false);
    enfileirar("r1");
    expect(JSON.parse(window.localStorage.getItem("f4.fila.v1") ?? "[]")).toHaveLength(1);

    recarregarFilaParaTestes();
    expect(lerFila().map((a) => a.id)).toEqual(["r1"]);

    limparFila();
    expect(lerFila()).toEqual([]);
    expect(JSON.parse(window.localStorage.getItem("f4.fila.v1") ?? "[]")).toEqual([]);
  });

  it("lets the waiter remove an action and ignores a repeated enqueue of the same id", async () => {
    online(false);

    enfileirar("r1");
    enfileirar("r1");
    expect(lerFila()).toHaveLength(1);
    removerAcao("r1");

    expect(lerFila()).toEqual([]);
  });

  it("sends when the signal returns, through the processor's online listener", async () => {
    stubRede({ "GET /comandas/c1/conta": contaOk(), "POST /comandas/c1/rodadas": rodadaOk("r1") });
    const concluidas: string[] = [];
    const parar = iniciarProcessador((a) => concluidas.push(a.id));
    online(false);
    enfileirar("r1");

    online(true);
    window.dispatchEvent(new Event("online"));
    await vi.waitFor(() => expect(lerFila()).toEqual([]));

    expect(concluidas).toEqual(["r1"]);
    parar();
  });

  it("sends a queued payment under its own id and closes the bill when nothing is left", async () => {
    const chamadas = stubRede({
      "GET /comandas/c1/conta": contaOk(4),
      "POST /comandas/c1/pagamentos": json(
        {
          id: "x",
          comandaId: "c1",
          turnoId: "t",
          metodo: "dinheiro",
          recebidoCentavos: 3000,
          aplicadoCentavos: 2750,
          trocoCentavos: 250,
          saldoRestanteCentavos: 0,
          comandaVersao: 5,
          registradoEm: new Date().toISOString(),
          operadorId: "o",
        },
        { status: 201 },
      ),
      "POST /comandas/c1/fechar": json({
        consumoCentavos: 0,
        taxaServicoCentavos: 0,
        totalCentavos: 0,
        pagoCentavos: 0,
        saldoCentavos: 0,
        versao: 6,
        estado: "fechada",
        taxaServicoRemovida: false,
        taxaServicoPercentual: 10,
      }),
    });

    enfileirarPagamento({
      pagamentoId: "0197e2e0-0000-7000-8000-000000000099",
      comandaId: "c1",
      recebidoCentavos: 3000,
      titulo: "Recebimento · Ana",
      detalhe: "Dinheiro R$ 30,00",
    });
    await vi.waitFor(() => expect(lerFila()).toEqual([]));

    expect(chamadas.map((c) => `${c.method} ${c.path}`)).toEqual([
      "GET /comandas/c1/conta",
      "POST /comandas/c1/pagamentos",
      "POST /comandas/c1/fechar",
    ]);
    expect(chamadas[1]!.body).toEqual({
      pagamentoId: "0197e2e0-0000-7000-8000-000000000099",
      recebidoCentavos: 3000,
      versao: 4,
    });
    expect(chamadas[2]!.body).toEqual({ versao: 5 });
  });

  it("keeps a payment that met a closed cash register as failed, with the reason", async () => {
    stubRede({
      "GET /comandas/c1/conta": contaOk(4),
      "POST /comandas/c1/pagamentos": json({ codigo: "caixa-fechado" }, { status: 409 }),
    });

    enfileirarPagamento({
      pagamentoId: "pg1",
      comandaId: "c1",
      recebidoCentavos: 1000,
      titulo: "Recebimento · Ana",
      detalhe: "Dinheiro R$ 10,00",
    });
    await vi.waitFor(() => expect(lerFila()[0]?.estado).toBe("falhou"));

    expect(lerFila()[0]?.motivo).toContain("caixa está fechado");
  });
});
