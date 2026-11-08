import { screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, describe, expect, it, vi } from "vitest";
import { type Chamada, json, renderEm, sessaoJson, stubRede, TODAS_AS_PERMISSOES } from "../../test-utils";
import { paraProduto } from "../comandas/cardapio";
import { Picker } from "../comandas/Picker";

afterEach(() => {
  vi.unstubAllGlobals();
});

const GERE = [...TODAS_AS_PERMISSOES, "produto:ver", "produto:gerenciar"];
const produto = (id: string, nome: string, categoriaId: string, extra: Record<string, unknown> = {}) => ({
  id,
  categoriaId,
  codigo: id.toUpperCase(),
  nome,
  precoCentavos: 1000,
  moeda: "BRL",
  ativo: true,
  versao: 3,
  indisponivel: false,
  promocao: null,
  precoDaUnidadeCentavos: null,
  precoEfetivoCentavos: 1000,
  ...extra,
});
const categoria = (id: string, nome: string, pai: string | null = null) => ({
  id,
  nome,
  ativa: true,
  versao: 1,
  categoriaPaiId: pai,
});

const base = (extra: Record<string, unknown> = {}) => ({
  "POST /refresh": json(sessaoJson()),
  "GET /comandas": json([]),
  "GET /sessao/permissoes": json({ permissoes: GERE }),
  "GET /cardapio/categorias": json([
    categoria("bebidas", "Bebidas"),
    categoria("cervejas", "Cervejas", "bebidas"),
    categoria("comidas", "Comidas"),
  ]),
  "GET /cardapio/produtos/gestao": json([
    produto("agua", "Água", "bebidas"),
    produto("chopp", "Chopp", "cervejas", { versao: 5 }),
    produto("pastel", "Pastel", "comidas", { ativo: false }),
  ]),
  ...extra,
});

describe("menu management screen", () => {
  it("lists products by category and subcategory, and searches by name or code", async () => {
    stubRede(base());
    await renderEm("/salao/cardapio");

    const titulos = await screen.findAllByRole("heading", { level: 3 });
    expect(titulos.map((t) => t.textContent)).toEqual(["Bebidas", "Cervejas", "Comidas"]);
    expect(within(screen.getByRole("list", { name: "Cervejas" })).getByText("Chopp")).toBeVisible();
    expect(screen.getByText("Inativo")).toBeVisible();

    await userEvent.type(screen.getByRole("searchbox", { name: "Buscar produto" }), "agua");
    expect(screen.getAllByRole("heading", { level: 3 }).map((t) => t.textContent)).toEqual(["Bebidas"]);

    await userEvent.clear(screen.getByRole("searchbox", { name: "Buscar produto" }));
    await userEvent.type(screen.getByRole("searchbox", { name: "Buscar produto" }), "zzz");
    expect(screen.getByText(/Nada com “zzz”/)).toBeVisible();
  });

  it("shows the tab only to who manages products", async () => {
    stubRede(base());
    await renderEm("/salao/cardapio");
    expect(await screen.findByRole("link", { name: "Cardápio" })).toBeVisible();
  });

  it("has no tab for who lacks produto:gerenciar, and the direct route says no without a reason", async () => {
    stubRede(base({ "GET /sessao/permissoes": json({ permissoes: TODAS_AS_PERMISSOES }) }));
    await renderEm("/salao/cardapio");
    expect(await screen.findByText("Você não tem permissão para isso.")).toBeVisible();
    expect(screen.queryByRole("link", { name: "Cardápio" })).toBeNull();
  });

  it("shows a bare 403 from the server as no permission", async () => {
    stubRede(base({ "GET /cardapio/produtos/gestao": new Response(null, { status: 403 }) }));
    await renderEm("/salao/cardapio");
    expect(await screen.findByText("Você não tem permissão para isso.")).toBeVisible();
  });

  it("shows an error with a way to retry, and the empty menu", async () => {
    stubRede(base({ "GET /cardapio/produtos/gestao": new Response("{}", { status: 500 }) }));
    const { unmount } = await renderEm("/salao/cardapio");
    expect(await screen.findByText("Não foi possível carregar o cardápio.")).toBeVisible();
    expect(screen.getByRole("button", { name: "Tentar novamente" })).toBeVisible();
    unmount();

    stubRede(base({ "GET /cardapio/produtos/gestao": json([]) }));
    await renderEm("/salao/cardapio");
    expect(await screen.findByText(/Nenhum produto no cardápio ainda/)).toBeVisible();
  });

  it("shows skeleton rows while loading", async () => {
    stubRede(base({ "GET /cardapio/produtos/gestao": () => new Promise<Response>(() => undefined) }));
    await renderEm("/salao/cardapio");
    expect(await screen.findByRole("status", { name: "Carregando o cardápio" })).toBeVisible();
  });

  it("marks a product as 'acabou' in one tap: sends the whole product with its version, shows the answer, and makes the waiter's grid stale", async () => {
    const chamadas = stubRede(
      base({
        "PUT /cardapio/produtos/:id": (c: Chamada) =>
          json({
            ...produto("agua", "Água", "bebidas", { versao: 4 }),
            indisponivel: (c.body as { indisponivel: boolean }).indisponivel,
          }),
      }),
    );
    const { queryClient } = await renderEm("/salao/cardapio");
    queryClient.setQueryData(["cardapio", "produtos"], []);

    await userEvent.click(await screen.findByRole("switch", { name: "Acabou: Água" }));

    await waitFor(() =>
      expect(screen.getByRole("switch", { name: "Acabou: Água" })).toHaveAttribute("aria-checked", "true"),
    );
    const escrita = chamadas.find((c) => c.method === "PUT");
    expect(escrita?.path).toBe("/cardapio/produtos/agua");
    expect(escrita?.body).toMatchObject({
      codigo: "AGUA",
      precoCentavos: 1000,
      ativo: true,
      indisponivel: true,
      versao: 3,
      promocao: null,
    });
    expect(within(screen.getByRole("list", { name: "Bebidas" })).getAllByText("Acabou")).toHaveLength(2);
    expect(queryClient.getQueryState(["cardapio", "produtos"])?.isInvalidated).toBe(true);
  });

  it("deactivates a product with the active switch", async () => {
    const chamadas = stubRede(
      base({ "PUT /cardapio/produtos/:id": json(produto("agua", "Água", "bebidas", { ativo: false, versao: 4 })) }),
    );
    await renderEm("/salao/cardapio");

    await userEvent.click(await screen.findByRole("switch", { name: "Ativo: Água" }));

    await waitFor(() =>
      expect(screen.getByRole("switch", { name: "Ativo: Água" })).toHaveAttribute("aria-checked", "false"),
    );
    expect(chamadas.find((c) => c.method === "PUT")?.body).toMatchObject({ ativo: false, indisponivel: false });
  });

  it("holds the switch while the request is in flight", async () => {
    let soltar: (r: Response) => void = () => undefined;
    stubRede(
      base({
        "PUT /cardapio/produtos/:id": () =>
          new Promise<Response>((r) => {
            soltar = r;
          }),
      }),
    );
    await renderEm("/salao/cardapio");

    await userEvent.click(await screen.findByRole("switch", { name: "Acabou: Água" }));
    expect(screen.getByRole("switch", { name: "Acabou: Água" })).toBeDisabled();
    expect(screen.getByRole("switch", { name: "Acabou: Água" })).toHaveAttribute("aria-busy", "true");
    soltar(json(produto("agua", "Água", "bebidas", { indisponivel: true, versao: 4 })));
    await waitFor(() => expect(screen.getByRole("switch", { name: "Acabou: Água" })).toBeEnabled());
  });

  it("shows the generic denied message on a 403 and leaves the switch as it was", async () => {
    stubRede(base({ "PUT /cardapio/produtos/:id": new Response(null, { status: 403 }) }));
    await renderEm("/salao/cardapio");

    await userEvent.click(await screen.findByRole("switch", { name: "Acabou: Água" }));

    expect(await screen.findByText("Você não tem permissão para isso.")).toBeVisible();
    expect(screen.getByRole("switch", { name: "Acabou: Água" })).toHaveAttribute("aria-checked", "false");
  });

  it("on a version conflict says the product changed elsewhere and reloads the list", async () => {
    const chamadas = stubRede(
      base({ "PUT /cardapio/produtos/:id": json({ codigo: "versao-desatualizada" }, { status: 409 }) }),
    );
    await renderEm("/salao/cardapio");

    await userEvent.click(await screen.findByRole("switch", { name: "Acabou: Água" }));

    expect(await screen.findByText(/foi alterado em outro aparelho/)).toBeVisible();
    await waitFor(() =>
      expect(chamadas.filter((c) => c.path === "/cardapio/produtos/gestao").length).toBeGreaterThanOrEqual(2),
    );
  });

  it("says there is no connection when the request cannot be made", async () => {
    stubRede(
      base({
        "PUT /cardapio/produtos/:id": () => {
          throw new TypeError("network");
        },
      }),
    );
    await renderEm("/salao/cardapio");
    await userEvent.click(await screen.findByRole("switch", { name: "Acabou: Água" }));
    expect(await screen.findByText("Sem conexão. Tente de novo.")).toBeVisible();
  });

  it("changes the price and sets a promotion in the sheet", async () => {
    const chamadas = stubRede(
      base({
        "PUT /cardapio/produtos/:id": json(
          produto("agua", "Água", "bebidas", { versao: 4, precoCentavos: 1250, precoEfetivoCentavos: 1250 }),
        ),
      }),
    );
    await renderEm("/salao/cardapio");

    await userEvent.click(await screen.findByRole("button", { name: "Preço de Água" }));
    const folha = await screen.findByRole("dialog", { name: "Preço de Água" });
    const normal = within(folha).getByLabelText("Preço normal");
    expect((normal as HTMLInputElement).value).toMatch(/^R\$\s10,00$/);
    await userEvent.clear(normal);
    await userEvent.type(normal, "1250");
    await userEvent.type(within(folha).getByLabelText("Preço promocional"), "900");
    await userEvent.type(within(folha).getByLabelText("Promoção começa"), "101120261000");
    await userEvent.type(within(folha).getByLabelText("Promoção termina"), "101120262200");
    await userEvent.click(within(folha).getByRole("button", { name: "Salvar preço" }));

    await waitFor(() => expect(screen.queryByRole("dialog")).toBeNull());
    const corpo = chamadas.find((c) => c.method === "PUT")?.body as {
      precoCentavos: number;
      promocao: { precoCentavos: number; inicio: string; fim: string };
    };
    expect(corpo.precoCentavos).toBe(1250);
    expect(corpo.promocao.precoCentavos).toBe(900);
    expect(corpo.promocao.inicio).toBe(new Date("2026-11-10T10:00").toISOString());
    expect(corpo.promocao.fim).toBe(new Date("2026-11-10T22:00").toISOString());
  });

  it("explains a bad promotion before sending anything", async () => {
    const chamadas = stubRede(base());
    await renderEm("/salao/cardapio");

    await userEvent.click(await screen.findByRole("button", { name: "Preço de Água" }));
    const folha = await screen.findByRole("dialog", { name: "Preço de Água" });
    await userEvent.type(within(folha).getByLabelText("Preço promocional"), "1500");
    await userEvent.click(within(folha).getByRole("button", { name: "Salvar preço" }));

    expect(await within(folha).findByText(/precisa ser menor que o preço normal/)).toBeVisible();
    expect(within(folha).getByText("Informe quando a promoção começa.")).toBeVisible();
    expect(chamadas.some((c) => c.method === "PUT")).toBe(false);
  });

  it("shows the generic denied message for a price above the limit, keeps the sheet open, no reason", async () => {
    stubRede(base({ "PUT /cardapio/produtos/:id": new Response(null, { status: 403 }) }));
    await renderEm("/salao/cardapio");

    await userEvent.click(await screen.findByRole("button", { name: "Preço de Água" }));
    const folha = await screen.findByRole("dialog", { name: "Preço de Água" });
    const normal = within(folha).getByLabelText("Preço normal");
    await userEvent.clear(normal);
    await userEvent.type(normal, "9900");
    await userEvent.click(within(folha).getByRole("button", { name: "Salvar preço" }));

    expect(await within(folha).findByRole("alert")).toHaveTextContent("Você não tem permissão para isso.");
    expect(screen.getByRole("dialog", { name: "Preço de Água" })).toBeVisible();
  });

  it("on a 409 in the price sheet keeps what was typed and the message, and the next save uses the fresh version", async () => {
    let gestao = 0;
    const chamadas = stubRede(
      base({
        "GET /cardapio/produtos/gestao": () =>
          json([produto("agua", "Água", "bebidas", { versao: gestao++ === 0 ? 3 : 4 })]),
        "PUT /cardapio/produtos/:id": (c: Chamada) =>
          (c.body as { versao: number }).versao === 3
            ? json({ codigo: "versao-desatualizada" }, { status: 409 })
            : json(produto("agua", "Água", "bebidas", { versao: 5, precoCentavos: 1250, precoEfetivoCentavos: 1250 })),
      }),
    );
    await renderEm("/salao/cardapio");

    await userEvent.click(await screen.findByRole("button", { name: "Preço de Água" }));
    const folha = await screen.findByRole("dialog", { name: "Preço de Água" });
    const normal = within(folha).getByLabelText("Preço normal");
    await userEvent.clear(normal);
    await userEvent.type(normal, "1250");
    await userEvent.click(within(folha).getByRole("button", { name: "Salvar preço" }));

    expect(await within(folha).findByRole("alert")).toHaveTextContent(/foi alterado em outro aparelho/);
    expect((within(folha).getByLabelText("Preço normal") as HTMLInputElement).value).toMatch(/^R\$\s12,50$/);

    await userEvent.click(within(folha).getByRole("button", { name: "Salvar preço" }));
    await waitFor(() => expect(screen.queryByRole("dialog")).toBeNull());
    const escritas = chamadas.filter((c) => c.method === "PUT").map((c) => (c.body as { versao: number }).versao);
    expect(escritas).toEqual([3, 4]);
  });

  it("warns that a unit price rules over the base price and the promotion", async () => {
    stubRede(
      base({
        "GET /cardapio/produtos/gestao": json([
          produto("agua", "Água", "bebidas", { precoDaUnidadeCentavos: 1100, precoEfetivoCentavos: 1100 }),
        ]),
      }),
    );
    await renderEm("/salao/cardapio");
    await userEvent.click(await screen.findByRole("button", { name: "Preço de Água" }));
    expect(await screen.findByText(/Esta unidade tem preço próprio de R\$\s11,00/)).toBeVisible();
  });
});

describe("waiter's product grid", () => {
  it("reads the effective price, not the base one, and the 'acabou' flag", () => {
    const p = paraProduto({
      id: "a",
      categoriaId: "c",
      codigo: "A",
      nome: "Água",
      precoCentavos: 1000,
      precoEfetivoCentavos: 800,
      indisponivel: true,
    });
    expect(p.precoCentavos).toBe(800);
    expect(p.indisponivel).toBe(true);
  });

  it("shows the effective price in the row", async () => {
    const { render } = await import("@testing-library/react");
    render(
      <Picker
        produtos={[
          paraProduto({
            id: "a",
            categoriaId: "c",
            codigo: "A",
            nome: "Água",
            precoCentavos: 1000,
            precoEfetivoCentavos: 800,
          }),
        ]}
        frequentes={[]}
        categorias={[{ id: "c", nome: "Bebidas" }]}
        quantidades={new Map()}
        onAdicionar={() => undefined}
        carregando={false}
        erro={false}
        onTentarDeNovo={() => undefined}
      />,
    );
    await userEvent.click(screen.getByRole("tab", { name: "Bebidas" }));
    expect(screen.getByText(/R\$\s8,00/)).toBeVisible();
    expect(screen.queryByText(/R\$\s10,00/)).toBeNull();
  });

  const noop = () => undefined;
  const grade = (produtos: { id: string; nome: string; indisponivel?: boolean }[]) => (
    <Picker
      produtos={produtos.map((p) => ({ categoriaId: "c", codigo: p.id.toUpperCase(), precoCentavos: 500, ...p }))}
      frequentes={[]}
      categorias={[{ id: "c", nome: "Bebidas" }]}
      quantidades={new Map()}
      onAdicionar={noop}
      carregando={false}
      erro={false}
      onTentarDeNovo={noop}
    />
  );

  it("marks a product that ran out in words and offers no add for it", async () => {
    const { render } = await import("@testing-library/react");
    render(
      grade([
        { id: "a", nome: "Água" },
        { id: "c", nome: "Chopp", indisponivel: true },
      ]),
    );
    await userEvent.click(screen.getByRole("tab", { name: "Bebidas" }));

    expect(screen.getByRole("button", { name: "Adicionar Água" })).toBeVisible();
    expect(screen.queryByRole("button", { name: "Adicionar Chopp" })).toBeNull();
    expect(screen.getByText("Acabou")).toBeVisible();
  });
});
