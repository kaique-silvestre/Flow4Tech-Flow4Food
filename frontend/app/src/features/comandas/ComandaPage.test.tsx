import { act, screen, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, describe, expect, it, vi } from "vitest";
import { formatBRL } from "@flow4tech/design-system";
import { json, renderEm, sessaoJson, stubRede } from "../../test-utils";
import { adicionar, definirQuantidade, totalEmPreparo, ultimaRodada, unidades, type ItemDaConta } from "./rodada";
import { buscarProdutos } from "./cardapio";
import { uuidv7 } from "./uuid";

afterEach(() => {
  vi.unstubAllGlobals();
  vi.useRealTimers();
});

const PERMISSOES_DO_GARCOM = ["comanda:abrir", "comanda:ver", "comanda:lancar-item", "comanda:cancelar-item", "comanda:desfazer-rodada", "mesa:ver", "produto:ver"];
const chopp = { id: "p1", categoriaId: "cat1", codigo: "CHOPP", nome: "Chopp 300ml", precoCentavos: 1250, moeda: "BRL", ativo: true, versao: 1 };
const batata = { id: "p2", categoriaId: "cat2", codigo: "BATATA", nome: "Porção de batata", precoCentavos: 3490, moeda: "BRL", ativo: true, versao: 1 };
const item = (id: string, extra: Record<string, unknown> = {}) => ({ id, rodadaId: "r1", posicao: 1, produtoId: "p1", produtoNome: "Chopp 300ml", precoCentavos: 1250, quantidade: 2, observacao: null, estado: "lancado", motivoCancelamento: null, canceladoPor: null, canceladoEm: null, ...extra });
const comandaJson = { id: "c1", unidadeId: "u1", numero: 7, numeroTexto: "#7", identificacao: "Ana", mesaId: null, canal: "salao", garcomId: "g1", pessoas: null, estado: "aberta", taxaServicoPercentual: 10, taxaServicoRemovida: false, versao: 2, abertaEm: new Date(Date.now() - 5 * 60_000).toISOString() };
const conta = (extra: Record<string, unknown> = {}) => ({ consumoCentavos: 2500, taxaServicoCentavos: 250, totalCentavos: 2750, pagoCentavos: 0, saldoCentavos: 2750, taxaServicoRemovida: false, taxaServicoPercentual: 10, versao: 2, estado: "aberta", ...extra });

function rotas(extra: Record<string, unknown> = {}, itens: unknown[] = [item("i1")]) {
  return {
    "POST /refresh": json(sessaoJson()),
    "GET /sessao/permissoes": json({ permissoes: PERMISSOES_DO_GARCOM }),
    "GET /comandas": json([comandaJson]),
    "GET /comandas/c1/conta": json(conta()),
    "GET /comandas/c1/itens": json({ itens, consumoCentavos: 2500 }),
    "GET /cardapio/produtos": json([chopp, batata]),
    "GET /cardapio/categorias": json([{ id: "cat1", nome: "Bebidas", ativa: true, versao: 1 }, { id: "cat2", nome: "Porções", ativa: true, versao: 1 }]),
    "GET /cardapio/produtos/frequentes": json([chopp]),
    ...extra,
  } as Record<string, Response | ((c: never) => Response | Promise<Response>)>;
}

async function abrir(extra: Record<string, unknown> = {}, itens?: unknown[]) {
  const chamadas = stubRede(rotas(extra, itens) as never);
  const r = await renderEm("/salao/comanda/c1");
  await screen.findByRole("heading", { level: 2, name: "Ana" });
  return { chamadas, ...r };
}

const rodadaOk = (versao = 3) => json({ id: "x", comandaId: "c1", enviadaEm: new Date().toISOString(), desfeitaEm: null, comandaVersao: versao, itens: [item("n1", { rodadaId: "x" })] }, { status: 201 });

describe("comanda screen layout", () => {
  it("shows the bill on top and the picker below: breadcrumb, total, badge, rows, search, tabs with Frequentes first, rows with price and +", async () => {
    await abrir();

    expect(document.querySelector("p.comanda-voltar")?.textContent).toBe("Comandas › #7");
    expect(screen.getByLabelText("Total da conta").textContent).toBe(formatBRL(2750));
    expect(screen.getByText("Aberta", { exact: false })).toBeVisible();
    const conta_ = screen.getByRole("list", { name: "Itens da conta" });
    expect(within(conta_).getByText("Chopp 300ml")).toBeVisible();
    expect(conta_.textContent).toContain(formatBRL(2500));
    expect(screen.getByLabelText("Buscar produto")).toBeVisible();
    const abas = screen.getAllByRole("tab").map((t) => t.textContent);
    expect(abas).toEqual(["Frequentes", "Bebidas", "Porções"]);
    expect(screen.getByRole("tab", { name: "Frequentes" })).toHaveAttribute("aria-selected", "true");
    expect(screen.getByRole("button", { name: "Adicionar Chopp 300ml" })).toBeVisible();
    expect(screen.getByText(/Serviço/).textContent).toContain(formatBRL(250));
  });

  it("starts on the first category when there are no frequent items yet", async () => {
    await abrir({ "GET /cardapio/produtos/frequentes": json([]) });

    expect(screen.getByRole("tab", { name: "Bebidas" })).toHaveAttribute("aria-selected", "true");
    expect(screen.getByRole("button", { name: "Adicionar Chopp 300ml" })).toBeVisible();
  });

  it("shows a skeleton while loading and an error with retry", async () => {
    let falhar = true;
    stubRede(rotas({ "GET /comandas/c1/conta": () => (falhar ? new Response(null, { status: 500 }) : json(conta())) }) as never);
    await renderEm("/salao/comanda/c1");

    expect(await screen.findByText("Não foi possível carregar a comanda.")).toBeVisible();
    falhar = false;
    await userEvent.click(screen.getByRole("button", { name: "Tentar novamente" }));

    expect(await screen.findByRole("heading", { level: 2, name: "Ana" })).toBeVisible();
  });

  it("says so when the comanda is not open or not the waiter's", async () => {
    stubRede(rotas({ "GET /comandas": json([]) }) as never);
    await renderEm("/salao/comanda/c1");

    expect(await screen.findByText("Esta comanda não está mais aberta ou não é sua.")).toBeVisible();
  });
});

describe("staging and the round bar", () => {
  it("stages with one tap, raises with the next, and the round bar says what is not sent", async () => {
    await abrir();
    expect(screen.queryByRole("region", { name: "Rodada não enviada" })).toBeNull();

    await userEvent.click(screen.getByRole("button", { name: "Adicionar Chopp 300ml" }));
    const barra = screen.getByRole("region", { name: "Rodada não enviada" });
    expect(barra).toHaveTextContent("1 item não enviado");
    expect(barra.textContent).toContain(formatBRL(1250));
    await userEvent.click(screen.getByRole("button", { name: "Adicionar Chopp 300ml" }));

    expect(barra).toHaveTextContent("2 itens não enviados");
    expect(screen.getByText("×2")).toBeVisible();
    expect(screen.getByRole("group", { name: "Chopp 300ml" })).toHaveTextContent("2");
    expect(screen.getByRole("button", { name: "Enviar" })).toHaveAttribute("data-variant", "primary");
  });

  it("changes the staged quantity with the stepper and drops the line at zero", async () => {
    await abrir();
    await userEvent.click(screen.getByRole("button", { name: "Adicionar Chopp 300ml" }));

    await userEvent.click(screen.getByRole("button", { name: "Aumentar Chopp 300ml" }));
    expect(screen.getByRole("region", { name: "Rodada não enviada" })).toHaveTextContent("2 itens");
    await userEvent.click(screen.getByRole("button", { name: "Diminuir Chopp 300ml" }));
    await userEvent.click(screen.getByRole("button", { name: "Diminuir Chopp 300ml" }));

    expect(screen.queryByRole("region", { name: "Rodada não enviada" })).toBeNull();
    expect(screen.queryByRole("group", { name: "Chopp 300ml" })).toBeNull();
  });

  it("searches by name or code, across categories, and says so when nothing matches", async () => {
    await abrir();
    const busca = screen.getByLabelText("Buscar produto");

    await userEvent.type(busca, "batata");
    expect(screen.getByRole("button", { name: "Adicionar Porção de batata" })).toBeVisible();
    expect(screen.queryByRole("button", { name: "Adicionar Chopp 300ml" })).toBeNull();
    await userEvent.clear(busca);
    await userEvent.type(busca, "xyz");

    expect(screen.getByText("Nada com “xyz”. Tente o código do produto.")).toBeVisible();
  });
});

describe("sending a round", () => {
  async function prepararEnvio(extra: Record<string, unknown> = {}) {
    const r = await abrir(extra);
    await userEvent.click(screen.getByRole("button", { name: "Adicionar Chopp 300ml" }));
    await userEvent.click(screen.getByRole("button", { name: "Adicionar Chopp 300ml" }));
    await userEvent.click(screen.getByRole("button", { name: "Enviar" }));
    const folha = await screen.findByRole("dialog", { name: "Enviar rodada" });
    return { ...r, folha };
  }

  it("opens the send sheet with the itemized round, then sends with the client round id and the comanda version", async () => {
    const { chamadas, folha } = await prepararEnvio({ "POST /comandas/c1/rodadas": rodadaOk() });
    expect(folha).toHaveAccessibleDescription("Vai para a cozinha e o bar. Você pode desfazer por 8 segundos.");
    const itens = within(folha).getByRole("list", { name: "Itens da rodada" });
    expect(itens).toHaveTextContent("2×");
    expect(itens.textContent).toContain(formatBRL(2500));

    await userEvent.click(within(folha).getByRole("button", { name: "Enviar agora" }));

    expect(await screen.findByText("Rodada enviada", { exact: false })).toBeVisible();
    const post = chamadas.find((c) => c.path === "/comandas/c1/rodadas" && c.method === "POST")!;
    expect(post.body).toMatchObject({ versao: 2, itens: [{ produtoId: "p1", quantidade: 2, observacao: null }] });
    expect((post.body as { rodadaId: string }).rodadaId).toMatch(/^[0-9a-f]{8}-[0-9a-f]{4}-7[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/);
    expect(screen.queryByRole("region", { name: "Rodada não enviada" })).toBeNull();
    expect(screen.queryByRole("dialog")).toBeNull();
  });

  it("keeps the round staged and says what did not go when the send fails, and a retry repeats the same round id", async () => {
    let tentativa = 0;
    const { chamadas, folha } = await prepararEnvio({
      "POST /comandas/c1/rodadas": () => {
        tentativa += 1;
        return tentativa === 1 ? new Response(null, { status: 500 }) : rodadaOk();
      },
    });

    await userEvent.click(within(folha).getByRole("button", { name: "Enviar agora" }));

    expect(await within(folha).findByText("Não foi possível enviar. A rodada continua aqui; tente de novo.")).toBeVisible();
    expect(within(folha).getByRole("list", { name: "Itens da rodada" })).toHaveTextContent("Chopp 300ml");
    expect(screen.getByRole("region", { name: "Rodada não enviada", hidden: true })).toHaveTextContent("2 itens");
    await userEvent.click(within(folha).getByRole("button", { name: "Enviar agora" }));
    await screen.findByText("Rodada enviada", { exact: false });
    const ids = chamadas.filter((c) => c.path === "/comandas/c1/rodadas" && c.method === "POST").map((c) => (c.body as { rodadaId: string }).rodadaId);
    expect(ids).toHaveLength(2);
    expect(ids[0]).toBe(ids[1]);
  });

  it("says the connection failed without losing the round", async () => {
    const { folha } = await prepararEnvio({ "POST /comandas/c1/rodadas": () => Promise.reject(new TypeError("network")) as never });

    await userEvent.click(within(folha).getByRole("button", { name: "Enviar agora" }));

    expect(await within(folha).findByText("Sem conexão. A rodada continua aqui; tente de novo.")).toBeVisible();
    expect(screen.getByRole("region", { name: "Rodada não enviada", hidden: true })).toBeInTheDocument();
  });

  it("tells a stale version without blocking, refreshes the bill and keeps what is staged", async () => {
    let contaVersao = 2;
    const { folha } = await prepararEnvio({
      "GET /comandas/c1/conta": () => json(conta({ versao: contaVersao, totalCentavos: 6000 })),
      "POST /comandas/c1/rodadas": () => {
        contaVersao = 5;
        return json({ codigo: "versao-desatualizada", versaoAtual: 5 }, { status: 409 });
      },
    });

    await userEvent.click(within(folha).getByRole("button", { name: "Enviar agora" }));

    expect(await screen.findByText(/foi alterada em outro aparelho/)).toBeVisible();
    expect(screen.getByRole("region", { name: "Rodada não enviada" })).toHaveTextContent("2 itens");
    expect((await screen.findByLabelText("Total da conta")).textContent).toBe(formatBRL(6000));
  });

  it("repeats the round the waiter sent last", async () => {
    await abrir({}, [item("i1", { rodadaId: "r1", quantidade: 2 }), item("i2", { rodadaId: "r2", quantidade: 1, produtoId: "p2", produtoNome: "Porção de batata", precoCentavos: 3490 })]);

    await userEvent.click(screen.getByRole("button", { name: "Repetir rodada" }));

    expect(screen.getByRole("region", { name: "Rodada não enviada" })).toHaveTextContent("1 item não enviado");
    expect(screen.getByRole("group", { name: "Porção de batata" })).toBeVisible();
    expect(screen.queryByRole("button", { name: "Repetir rodada" })).toBeNull();
  });
});

describe("undo", () => {
  async function enviarUmChopp(extra: Record<string, unknown>) {
    const chamadas = stubRede(rotas({ "POST /comandas/c1/rodadas": rodadaOk(3), ...extra }) as never);
    await renderEm("/salao/comanda/c1");
    await screen.findByRole("heading", { level: 2, name: "Ana" });
    await userEvent.click(screen.getByRole("button", { name: "Adicionar Chopp 300ml" }));
    await userEvent.click(screen.getByRole("button", { name: "Enviar" }));
    await userEvent.click(await screen.findByRole("button", { name: "Enviar agora" }));
    const toast = (await screen.findByText("Rodada enviada", { exact: false })).closest('[role="status"]') as HTMLElement;
    return { chamadas, toast };
  }

  it("offers the undo for eight seconds and undoes the round carrying the version the send answered", async () => {
    const { chamadas, toast } = await enviarUmChopp({ "POST /comandas/c1/rodadas/:id/desfazer": json({ id: "x", comandaId: "c1", enviadaEm: new Date().toISOString(), desfeitaEm: new Date().toISOString(), comandaVersao: 4, itens: [] }) });
    expect(toast).toHaveTextContent("(8 s)");

    await userEvent.click(within(toast).getByRole("button", { name: "Desfazer" }));

    expect(await screen.findByText("Rodada desfeita. Os itens saíram da conta.")).toBeVisible();
    const enviada = chamadas.find((c) => c.path === "/comandas/c1/rodadas" && c.method === "POST")!;
    const desfazer = chamadas.find((c) => c.path.endsWith("/desfazer"))!;
    expect(desfazer.path).toBe(`/comandas/c1/rodadas/${(enviada.body as { rodadaId: string }).rodadaId}/desfazer`);
    expect(desfazer.body).toEqual({ versao: 3 });
    expect(screen.queryByRole("button", { name: "Desfazer" })).toBeNull();
  });

  it("removes the action when the window ends", async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    const { toast } = await enviarUmChopp({});
    expect(within(toast).getByRole("button", { name: "Desfazer" })).toBeEnabled();

    await act(async () => {
      await vi.advanceTimersByTimeAsync(8500);
    });

    expect(screen.queryByRole("button", { name: "Desfazer" })).toBeNull();
  });

  it("says the time is over when the server refuses the undo", async () => {
    const { toast } = await enviarUmChopp({ "POST /comandas/c1/rodadas/:id/desfazer": json({ codigo: "janela-expirada", versaoAtual: null }, { status: 409 }) });

    await userEvent.click(within(toast).getByRole("button", { name: "Desfazer" }));

    expect(await screen.findByText("Passou o prazo para desfazer a rodada.")).toBeVisible();
  });

  it("has no undo for a caller without the permission", async () => {
    stubRede(rotas({ "GET /sessao/permissoes": json({ permissoes: ["comanda:ver", "comanda:lancar-item", "produto:ver"] }), "POST /comandas/c1/rodadas": rodadaOk(3) }) as never);
    await renderEm("/salao/comanda/c1");
    await screen.findByRole("heading", { level: 2, name: "Ana" });
    await userEvent.click(screen.getByRole("button", { name: "Adicionar Chopp 300ml" }));
    await userEvent.click(screen.getByRole("button", { name: "Enviar" }));
    await userEvent.click(await screen.findByRole("button", { name: "Enviar agora" }));

    await vi.waitFor(() => expect(screen.queryByRole("dialog")).toBeNull());
    expect(screen.queryByRole("button", { name: "Desfazer" })).toBeNull();
  });
});

describe("cancelling an item", () => {
  async function abrirCancelar(extra: Record<string, unknown> = {}) {
    const r = await abrir(extra);
    await userEvent.click(screen.getByRole("button", { name: "Cancelar Chopp 300ml" }));
    return { ...r, folha: await screen.findByRole("dialog", { name: "Cancelar item" }) };
  }

  it("asks for a reason from the standard list and keeps the button off until there is one", async () => {
    const { folha } = await abrirCancelar();

    expect(within(folha).getAllByRole("button", { pressed: false }).map((b) => b.textContent)).toEqual(["Cliente desistiu", "Erro de lançamento", "Item indisponível", "Outro"]);
    expect(within(folha).getByRole("button", { name: "Cancelar item" })).toBeDisabled();
    await userEvent.click(within(folha).getByRole("button", { name: "Item indisponível" }));
    expect(within(folha).getByRole("button", { name: "Cancelar item" })).toBeEnabled();
  });

  it("sends the code, the version and the free text of Outro, and the item leaves the bill", async () => {
    let cancelado = false;
    const { chamadas, folha } = await abrirCancelar({
      "GET /comandas/c1/itens": () => json({ itens: cancelado ? [item("i1", { estado: "cancelado" })] : [item("i1")], consumoCentavos: 0 }),
      "POST /comandas/c1/itens/i1/cancelar": () => {
        cancelado = true;
        return json({ item: item("i1", { estado: "cancelado" }), comandaVersao: 3 });
      },
    });
    await userEvent.click(within(folha).getByRole("button", { name: "Outro" }));
    expect(within(folha).getByRole("button", { name: "Cancelar item" })).toBeDisabled();
    await userEvent.type(within(folha).getByLabelText("Qual o motivo?"), "cliente trocou de mesa");
    await userEvent.click(within(folha).getByRole("button", { name: "Cancelar item" }));

    await vi.waitFor(() => expect(screen.queryByRole("dialog")).toBeNull());
    expect(chamadas.find((c) => c.path === "/comandas/c1/itens/i1/cancelar")?.body).toEqual({ versao: 2, motivo: "outro", texto: "cliente trocou de mesa" });
    await vi.waitFor(() => expect(screen.queryByRole("button", { name: "Cancelar Chopp 300ml" })).toBeNull());
  });

  it("keeps the sheet open and says why when the item was already paid", async () => {
    const { folha } = await abrirCancelar({ "POST /comandas/c1/itens/i1/cancelar": json({ codigo: "valor-ja-recebido", versaoAtual: null }, { status: 409 }) });
    await userEvent.click(within(folha).getByRole("button", { name: "Cliente desistiu" }));

    await userEvent.click(within(folha).getByRole("button", { name: "Cancelar item" }));

    expect(await within(folha).findByText("Este item já foi pago na conta e não pode ser cancelado.")).toBeVisible();
  });
});

describe("permissions", () => {
  it("has no picker, no round bar and no repeat for a caller who may not launch", async () => {
    stubRede(rotas({ "GET /sessao/permissoes": json({ permissoes: ["comanda:ver"] }) }) as never);
    await renderEm("/salao/comanda/c1");
    await screen.findByRole("heading", { level: 2, name: "Ana" });

    expect(screen.queryByLabelText("Buscar produto")).toBeNull();
    expect(screen.queryByRole("button", { name: "Repetir rodada" })).toBeNull();
    expect(screen.queryByRole("button", { name: "Cancelar Chopp 300ml" })).toBeNull();
  });
});

describe("round rules", () => {
  const p = { id: "p1", categoriaId: "c", codigo: "A", nome: "A", precoCentavos: 1000 };
  const q = { id: "p2", categoriaId: "c", codigo: "B", nome: "B", precoCentavos: 500 };

  it("keeps one line per product, sums units and value, and drops a line at zero", () => {
    let r = adicionar([], p);
    r = adicionar(r, p);
    r = adicionar(r, q);

    expect(r.map((i) => [i.produto.id, i.quantidade])).toEqual([["p1", 2], ["p2", 1]]);
    expect(unidades(r)).toBe(3);
    expect(totalEmPreparo(r)).toBe(2500);
    expect(definirQuantidade(r, "p1", 0).map((i) => i.produto.id)).toEqual(["p2"]);
    expect(definirQuantidade(r, "p2", 5)[1]?.quantidade).toBe(5);
  });

  it("stops at 999 units and 50 lines", () => {
    let r = [{ produto: p, quantidade: 999 }];
    r = adicionar(r, p);
    expect(r[0]?.quantidade).toBe(999);
    const cheio = Array.from({ length: 50 }, (_, i) => ({ produto: { ...p, id: `x${i}` }, quantidade: 1 }));
    expect(adicionar(cheio, q)).toHaveLength(50);
  });

  it("finds the last standing round, ignoring cancelled items", () => {
    const base: ItemDaConta = { id: "1", rodadaId: "r1", produtoId: "p1", produtoNome: "A", precoCentavos: 1, quantidade: 1, observacao: null, estado: "lancado" };
    const itens: ItemDaConta[] = [base, { ...base, id: "2", rodadaId: "r2" }, { ...base, id: "3", rodadaId: "r2", produtoId: "p2" }, { ...base, id: "4", rodadaId: "r3", estado: "cancelado" }];

    expect(ultimaRodada(itens)?.map((i) => i.id)).toEqual(["2", "3"]);
    expect(ultimaRodada([])).toBeNull();
  });

  it("searches products by name or code without accents", () => {
    const lista = [{ ...p, nome: "Porção de batata", codigo: "BATATA" }, q];

    expect(buscarProdutos(lista, "porcao").map((x) => x.id)).toEqual(["p1"]);
    expect(buscarProdutos(lista, "b").map((x) => x.id)).toEqual(["p1", "p2"]);
    expect(buscarProdutos(lista, "")).toHaveLength(2);
  });

  it("makes UUID v7 ids that are time-ordered and well formed", () => {
    const a = uuidv7(1_700_000_000_000);
    const b = uuidv7(1_700_000_001_000);

    expect(a).toMatch(/^[0-9a-f]{8}-[0-9a-f]{4}-7[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/);
    expect(a < b).toBe(true);
    expect(uuidv7(1_700_000_000_000)).not.toBe(a);
    expect(act).toBeDefined();
  });
});
