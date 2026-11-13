import { onlineManager } from "@tanstack/react-query";
import { screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, describe, expect, it, vi } from "vitest";
import { json, renderEm, sessaoJson, stubRede } from "../../test-utils";
import { lerFila } from "../fila/fila";
import { horaDoEvento, linhaDoHistorico } from "./historico";

afterEach(() => {
  vi.unstubAllGlobals();
  Object.defineProperty(window.navigator, "onLine", { configurable: true, value: true });
  onlineManager.setOnline(true);
});

const BASE = ["comanda:ver", "comanda:cancelar-item", "mesa:ver", "produto:ver"];
const TUDO = [
  ...BASE,
  "comanda:alterar-item",
  "comanda:renomear",
  "comanda:alterar-dados",
  "comanda:trocar-garcom",
  "comanda:ver-historico",
  "comanda:cancelar",
];
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
  observacao: "aniversário",
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
};

type Rotas = Record<string, Response | ((c: never) => Response | Promise<Response>)>;

async function abrir(permissoes: string[], extra: Record<string, unknown> = {}) {
  const chamadas = stubRede({
    "POST /refresh": json(sessaoJson()),
    "GET /sessao/permissoes": json({ permissoes }),
    "GET /comandas": json([comandaJson]),
    "GET /comandas/c1/conta": json(conta),
    "GET /comandas/c1/itens": json({ itens: [item], consumoCentavos: 2500 }),
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

describe("what is offered depends on the permission", () => {
  it("shows no edit button and no menu without any of the new permissions", async () => {
    await abrir(BASE);

    expect(await screen.findByText("Chopp 300ml")).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Alterar Chopp 300ml" })).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Mais" })).not.toBeInTheDocument();
  });

  it("lists in the menu only the actions the caller holds", async () => {
    await abrir([...BASE, "comanda:renomear", "comanda:ver-historico"]);
    const user = userEvent.setup();

    await user.click(await screen.findByRole("button", { name: "Mais" }));

    const lista = within(await screen.findByRole("list", { name: "Ações da comanda" }));
    expect(lista.getAllByRole("button").map((b) => b.textContent)).toEqual(["Renomear comanda", "Histórico"]);
  });

  it("lists every action with every permission", async () => {
    await abrir(TUDO);
    const user = userEvent.setup();

    await user.click(await screen.findByRole("button", { name: "Mais" }));

    const lista = within(await screen.findByRole("list", { name: "Ações da comanda" }));
    expect(lista.getAllByRole("button").map((b) => b.textContent)).toEqual([
      "Renomear comanda",
      "Pessoas e observação",
      "Trocar de garçom",
      "Histórico",
      "Cancelar comanda",
    ]);
  });
});

describe("edits go through the queue", () => {
  it("alters an item: quantity and note leave in the queue and are sent with the version read at send time", async () => {
    const chamadas = await abrir(TUDO, {
      "POST /comandas/c1/itens/i1/alterar": json({
        comandaVersao: 3,
        item: { ...item, quantidade: 3, observacao: "sem gelo" },
      }),
    });
    const user = userEvent.setup();

    await user.click(await screen.findByRole("button", { name: "Alterar Chopp 300ml" }));
    const folha = await screen.findByRole("dialog", { name: "Alterar item" });
    expect(within(folha).getByRole("button", { name: "Salvar" })).toBeDisabled();
    await user.click(within(folha).getByRole("button", { name: "Aumentar Chopp 300ml" }));
    await user.type(within(folha).getByLabelText("Observação"), "sem gelo");
    await user.click(within(folha).getByRole("button", { name: "Salvar" }));

    await waitFor(() =>
      expect(corpoDe(chamadas, "/itens/i1/alterar")).toEqual({ versao: 2, quantidade: 3, observacao: "sem gelo" }),
    );
    expect(screen.queryByRole("dialog", { name: "Alterar item" })).not.toBeInTheDocument();
  });

  it("renames, and refuses an empty or unchanged name", async () => {
    const chamadas = await abrir(TUDO, {
      "POST /comandas/c1/renomear": json({ ...comandaJson, identificacao: "Mesa da Ana", versao: 3 }),
    });
    const user = userEvent.setup();

    await user.click(await screen.findByRole("button", { name: "Mais" }));
    await user.click(await screen.findByRole("button", { name: "Renomear comanda" }));
    const folha = await screen.findByRole("dialog", { name: "Renomear comanda" });
    const campo = within(folha).getByLabelText("Nome da comanda");
    expect(within(folha).getByRole("button", { name: "Salvar" })).toBeDisabled();
    await user.clear(campo);
    expect(within(folha).getByRole("button", { name: "Salvar" })).toBeDisabled();
    await user.type(campo, "  Mesa   da Ana ");
    await user.click(within(folha).getByRole("button", { name: "Salvar" }));

    await waitFor(() => expect(corpoDe(chamadas, "/renomear")).toEqual({ versao: 2, identificacao: "Mesa da Ana" }));
    expect(lerFila().filter((a) => a.tipo === "edicao")).toHaveLength(0);
  });

  it("changes the head count and the note together and can clear both", async () => {
    const chamadas = await abrir(TUDO, {
      "POST /comandas/c1/dados": json({ ...comandaJson, pessoas: null, observacao: null, versao: 3 }),
    });
    const user = userEvent.setup();

    await user.click(await screen.findByRole("button", { name: "Mais" }));
    await user.click(await screen.findByRole("button", { name: "Pessoas e observação" }));
    const folha = await screen.findByRole("dialog", { name: "Pessoas e observação" });
    expect(within(folha).getByLabelText("Número de pessoas")).toHaveValue("2");
    expect(within(folha).getByLabelText("Observação da comanda")).toHaveValue("aniversário");
    await user.clear(within(folha).getByLabelText("Número de pessoas"));
    await user.clear(within(folha).getByLabelText("Observação da comanda"));
    await user.click(within(folha).getByRole("button", { name: "Salvar" }));

    await waitFor(() => expect(corpoDe(chamadas, "/dados")).toEqual({ versao: 2, pessoas: null, observacao: null }));
  });

  it("hands the comanda to another account: the current waiter is not offered", async () => {
    const chamadas = await abrir(TUDO, {
      "GET /comandas/garcons": json([
        { id: "g1", nome: "Joana" },
        { id: "g2", nome: "Marcos" },
      ]),
      "POST /comandas/c1/trocar-garcom": json({ ...comandaJson, garcomId: "g2", versao: 3 }),
    });
    const user = userEvent.setup();

    await user.click(await screen.findByRole("button", { name: "Mais" }));
    await user.click(await screen.findByRole("button", { name: "Trocar de garçom" }));
    const folha = await screen.findByRole("dialog", { name: "Trocar de garçom" });
    expect(await within(folha).findByRole("button", { name: "Marcos" })).toBeInTheDocument();
    expect(within(folha).queryByRole("button", { name: "Joana" })).not.toBeInTheDocument();
    expect(within(folha).getByRole("button", { name: "Trocar" })).toBeDisabled();
    await user.click(within(folha).getByRole("button", { name: "Marcos" }));
    await user.click(within(folha).getByRole("button", { name: "Trocar" }));

    await waitFor(() => expect(corpoDe(chamadas, "/trocar-garcom")).toEqual({ versao: 2, garcomId: "g2" }));
  });

  it("tells when the team could not be read and tries again", async () => {
    let tentativa = 0;
    await abrir(TUDO, {
      "GET /comandas/garcons": () => {
        tentativa += 1;
        return tentativa === 1 ? json({}, { status: 500 }) : json([{ id: "g2", nome: "Marcos" }]);
      },
    });
    const user = userEvent.setup();

    await user.click(await screen.findByRole("button", { name: "Mais" }));
    await user.click(await screen.findByRole("button", { name: "Trocar de garçom" }));
    const folha = await screen.findByRole("dialog", { name: "Trocar de garçom" });
    await user.click(await within(folha).findByRole("button", { name: "Tentar novamente" }));

    expect(await within(folha).findByRole("button", { name: "Marcos" })).toBeInTheDocument();
  });
});

describe("history", () => {
  const abrirHistorico = async (historico: Rotas[string]) => {
    await abrir(TUDO, { "GET /comandas/c1/historico": historico });
    const user = userEvent.setup();
    await user.click(await screen.findByRole("button", { name: "Mais" }));
    await user.click(await screen.findByRole("button", { name: "Histórico" }));
    return { user, folha: await screen.findByRole("dialog", { name: "Histórico" }) };
  };

  it("shows the timeline in the words of the floor with who did it", async () => {
    const { folha } = await abrirHistorico(
      json({
        eventos: [
          {
            id: "e1",
            acao: "comanda:abrir",
            quando: new Date().toISOString(),
            usuarioId: "g1",
            usuarioNome: "Joana",
            antes: {},
            depois: { identificacao: "Ana" },
          },
          {
            id: "e2",
            acao: "comanda:lancar-rodada",
            quando: new Date().toISOString(),
            usuarioId: "g1",
            usuarioNome: "Joana",
            antes: {},
            depois: { itens: "2", valorCentavos: "2500" },
          },
          {
            id: "e3",
            acao: "comanda:renomear",
            quando: new Date().toISOString(),
            usuarioId: "g2",
            usuarioNome: "Marcos",
            antes: { identificacao: "Ana" },
            depois: { identificacao: "Mesa da Ana" },
          },
        ],
      }),
    );

    const linhas = within(await within(folha).findByRole("list", { name: "Linha do tempo da comanda" })).getAllByRole(
      "listitem",
    );
    expect(linhas).toHaveLength(3);
    expect(linhas[0]).toHaveTextContent("Comanda aberta");
    expect(linhas[1]).toHaveTextContent("Rodada enviada");
    expect(linhas[1]).toHaveTextContent("R$ 25,00");
    expect(linhas[2]).toHaveTextContent("Comanda renomeada");
    expect(linhas[2]).toHaveTextContent("Ana → Mesa da Ana");
    expect(linhas[2]).toHaveTextContent("Marcos");
  });

  it("shows an empty state and an error with a way to retry", async () => {
    const vazio = await abrirHistorico(json({ eventos: [] }));
    expect(await within(vazio.folha).findByText("Nada registrado ainda.")).toBeInTheDocument();
  });

  it("offers to try again when the history could not be read", async () => {
    let tentativa = 0;
    const { user, folha } = await abrirHistorico(() => {
      tentativa += 1;
      return tentativa === 1 ? json({}, { status: 500 }) : json({ eventos: [] });
    });

    await user.click(await within(folha).findByRole("button", { name: "Tentar novamente" }));

    expect(await within(folha).findByText("Nada registrado ainda.")).toBeInTheDocument();
  });
});

describe("cancelling the comanda", () => {
  async function abrirCancelar(extra: Record<string, unknown> = {}) {
    const chamadas = await abrir(TUDO, extra);
    const user = userEvent.setup();
    await user.click(await screen.findByRole("button", { name: "Mais" }));
    await user.click(await screen.findByRole("button", { name: "Cancelar comanda" }));
    return { chamadas, user, folha: await screen.findByRole("dialog", { name: "Cancelar comanda" }) };
  }

  it("needs a reason, then an explicit confirmation, and leaves the comanda when the server accepts", async () => {
    const { chamadas, user, folha } = await abrirCancelar({
      "POST /comandas/c1/cancelar": json({
        comanda: { ...comandaJson, estado: "cancelada", versao: 3 },
        itensCancelados: 1,
      }),
    });
    expect(within(folha).getByText(/precisa de conexão/)).toBeInTheDocument();
    expect(within(folha).getByRole("button", { name: "Continuar" })).toBeDisabled();

    await user.click(within(folha).getByRole("button", { name: "Erro de lançamento" }));
    await user.click(within(folha).getByRole("button", { name: "Continuar" }));
    const confirmacao = await screen.findByRole("dialog", { name: "Cancelar a comanda #7?" });
    expect(chamadas.some((c) => c.path.endsWith("/cancelar"))).toBe(false);
    await user.click(within(confirmacao).getByRole("button", { name: "Cancelar comanda" }));

    await waitFor(() =>
      expect(corpoDe(chamadas, "/cancelar")).toEqual({ versao: 2, motivo: "erro-de-lancamento", texto: null }),
    );
    await waitFor(() => expect(screen.queryByRole("heading", { level: 2, name: "Ana" })).not.toBeInTheDocument());
  });

  it("asks for the text with 'Outro'", async () => {
    const { user, folha } = await abrirCancelar();

    await user.click(within(folha).getByRole("button", { name: "Outro" }));
    expect(within(folha).getByRole("button", { name: "Continuar" })).toBeDisabled();
    await user.type(within(folha).getByLabelText("Qual o motivo?"), "mesa errada");

    expect(within(folha).getByRole("button", { name: "Continuar" })).toBeEnabled();
  });

  it("says why a comanda with a payment is not cancelled and keeps the sheet open", async () => {
    const { user, folha } = await abrirCancelar({
      "POST /comandas/c1/cancelar": json({ codigo: "com-pagamentos" }, { status: 409 }),
    });

    await user.click(within(folha).getByRole("button", { name: "Cliente desistiu" }));
    await user.click(within(folha).getByRole("button", { name: "Continuar" }));
    await user.click(
      within(await screen.findByRole("dialog", { name: "Cancelar a comanda #7?" })).getByRole("button", {
        name: "Cancelar comanda",
      }),
    );

    expect(await screen.findByText("Esta comanda já tem pagamento e não pode ser cancelada.")).toBeInTheDocument();
    expect(screen.getByRole("heading", { level: 2, name: "Ana", hidden: true })).toBeInTheDocument();
  });

  it("refreshes and says so on a stale version", async () => {
    const { user, folha } = await abrirCancelar({
      "POST /comandas/c1/cancelar": json({ codigo: "versao-desatualizada", versaoAtual: 5 }, { status: 409 }),
    });

    await user.click(within(folha).getByRole("button", { name: "Cliente desistiu" }));
    await user.click(within(folha).getByRole("button", { name: "Continuar" }));
    await user.click(
      within(await screen.findByRole("dialog", { name: "Cancelar a comanda #7?" })).getByRole("button", {
        name: "Cancelar comanda",
      }),
    );

    expect(await screen.findByText(/foi alterada em outro aparelho/)).toBeInTheDocument();
  });

  it("is blocked, and says so, without a connection", async () => {
    await abrir(TUDO);
    const user = userEvent.setup();
    Object.defineProperty(window.navigator, "onLine", { configurable: true, value: false });
    await user.click(await screen.findByRole("button", { name: "Mais" }));
    await user.click(await screen.findByRole("button", { name: "Cancelar comanda" }));
    const folha = await screen.findByRole("dialog", { name: "Cancelar comanda" });

    await user.click(within(folha).getByRole("button", { name: "Cliente desistiu" }));

    expect(within(folha).getByText("Sem conexão. Cancelar a comanda precisa do servidor.")).toBeInTheDocument();
    expect(within(folha).getByRole("button", { name: "Continuar" })).toBeDisabled();
  });
});

describe("the timeline wording", () => {
  it.each([
    [
      "comanda:cancelar-item",
      {},
      { produto: "Chopp", motivo: "erro de lançamento" },
      "Item cancelado",
      "Chopp · erro de lançamento",
    ],
    [
      "comanda:alterar-item",
      { quantidade: "2" },
      { produto: "Chopp", quantidade: "5", observacao: "sem gelo" },
      "Item alterado",
      "Chopp · 2 → 5 un. · sem gelo",
    ],
    ["pagamento:registrar", {}, { aplicadoCentavos: "1000" }, "Pagamento recebido", "R$ 10,00"],
    ["comanda:fechar", {}, { totalCentavos: "2750" }, "Conta fechada", "R$ 27,50"],
    ["comanda:cancelar", {}, { motivo: "cliente desistiu" }, "Comanda cancelada", "cliente desistiu"],
    ["comanda:alterar-dados", {}, { observacao: "bolo" }, "Dados alterados", "sem número de pessoas · bolo"],
    ["comanda:futura", {}, {}, "comanda:futura", null],
  ])("%s reads as %s", (acao, antes, depois, titulo, detalhe) => {
    const linha = linhaDoHistorico({ acao, antes, depois });
    expect(linha.titulo).toBe(titulo);
    const claro = (texto: string | null | undefined) => texto?.replace(/\s/g, " ") ?? null;
    expect(claro(linha.detalhe)).toBe(claro(detalhe));
  });

  it("shows the time of today and the date of another day", () => {
    const agora = new Date(2026, 10, 12, 22, 0);
    expect(horaDoEvento(new Date(2026, 10, 12, 21, 34).toISOString(), agora)).toBe("21:34");
    expect(horaDoEvento(new Date(2026, 10, 11, 9, 5).toISOString(), agora)).toBe("11/11 09:05");
    expect(horaDoEvento("lixo", agora)).toBe("");
  });
});
