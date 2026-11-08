import { screen, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, describe, expect, it, vi } from "vitest";
import { json, renderEm, sessaoJson, stubRede, TODAS_AS_PERMISSOES, tokenDe } from "../../test-utils";

afterEach(() => {
  vi.unstubAllGlobals();
});

const U1 = "0197e2e0-0000-7000-8000-0000000000a1";
const pronta = { "POST /refresh": json(sessaoJson()), "GET /comandas": json([]) };
const COM_PERFIS = [...TODAS_AS_PERMISSOES, "perfil:ver", "perfil:gerir", "permissao:atribuir"];
const perfil = (id: string, nome: string, n = 2) => ({
  id,
  nome,
  modelo: nome.toLowerCase(),
  acompanhaModelo: true,
  regras: Array.from({ length: n }, (_, i) => ({ efeito: "allow", permissao: `comanda:x${i}` })),
});
const pessoa = (id: string, nome: string, extra: Record<string, unknown> = {}) => ({
  id,
  nome,
  usuario: nome.toLowerCase(),
  dono: false,
  ativa: true,
  unidadeIds: [U1],
  perfilIds: [],
  ...extra,
});
const base = (extra: Record<string, unknown> = {}) => ({
  ...pronta,
  "GET /sessao/permissoes": json({ permissoes: COM_PERFIS }),
  "GET /unidades": json([{ id: U1, nome: "Centro" }]),
  "GET /perfis": json([perfil("g", "Garçom", 3), perfil("c", "Caixa")]),
  "GET /contas": json([pessoa("p1", "Ana", { perfilIds: ["g"] }), pessoa("p2", "Bia")]),
  ...extra,
});

describe("pessoas screen", () => {
  it("lists people with profile and unit, and marks who has none", async () => {
    stubRede(base());
    await renderEm("/salao/pessoas");

    const lista = await screen.findByRole("list", { name: "Pessoas" });
    const ana = within(within(lista).getByRole("button", { name: /^Ana/ }));
    expect(ana.getByText("Garçom")).toBeVisible();
    expect(ana.getByText(/Centro/)).toBeVisible();
    expect(within(lista).getByRole("button", { name: /^Bia/ })).toHaveTextContent("Sem perfil");
  });

  it("shows the tab only to who may see profiles", async () => {
    stubRede(base());
    await renderEm("/salao/pessoas");
    expect(await screen.findByRole("link", { name: "Pessoas" })).toBeVisible();
  });

  it("has no Pessoas tab for who lacks perfil:ver, and the direct route says no without a reason", async () => {
    stubRede(base());
    stubRede({ ...base(), "GET /sessao/permissoes": json({ permissoes: TODAS_AS_PERMISSOES }) });
    await renderEm("/salao/pessoas");
    expect(await screen.findByText("Você não tem permissão para isso.")).toBeVisible();
    expect(screen.queryByRole("link", { name: "Pessoas" })).toBeNull();
  });

  it("shows a bare 403 from the server as no permission, with no reason", async () => {
    stubRede(base({ "GET /contas": new Response(null, { status: 403 }) }));
    await renderEm("/salao/pessoas");
    expect(await screen.findByText("Você não tem permissão para isso.")).toBeVisible();
  });

  it("shows loading as skeleton rows, then an error with a way to retry", async () => {
    stubRede(base({ "GET /contas": new Response("{}", { status: 500 }) }));
    await renderEm("/salao/pessoas");
    expect(await screen.findByText("Não foi possível carregar as pessoas.")).toBeVisible();
    expect(screen.getByRole("button", { name: "Tentar novamente" })).toBeVisible();
  });

  it("offers 'Criar perfis modelo' only when there are no profiles, and refreshes after", async () => {
    let criados = false;
    const chamadas = stubRede(
      base({
        "GET /perfis": () => json(criados ? [perfil("g", "Garçom")] : []),
        "POST /perfis/modelos": () => {
          criados = true;
          return json([{ chave: "garcom", nome: "Garçom", criado: true, perfilId: "g" }], { status: 201 });
        },
      }),
    );
    await renderEm("/salao/pessoas");

    await userEvent.click(await screen.findByRole("button", { name: "Criar perfis modelo" }));

    expect(await screen.findByText(/Perfis criados: Garçom/)).toBeVisible();
    expect(chamadas.some((c) => c.method === "POST" && c.path === "/perfis/modelos")).toBe(true);
    expect(screen.queryByRole("button", { name: "Criar perfis modelo" })).toBeNull();
  });

  it("does not offer the shortcut to who cannot manage profiles", async () => {
    stubRede(
      base({
        "GET /perfis": json([]),
        "GET /sessao/permissoes": json({ permissoes: [...TODAS_AS_PERMISSOES, "perfil:ver", "permissao:atribuir"] }),
      }),
    );
    await renderEm("/salao/pessoas");
    await screen.findByRole("list", { name: "Pessoas" });
    expect(screen.queryByRole("button", { name: "Criar perfis modelo" })).toBeNull();
  });

  it("assigns and removes a profile in the sheet", async () => {
    const chamadas = stubRede(
      base({
        "PUT /contas/:id/perfis/:pid": new Response(null, { status: 204 }),
        "DELETE /contas/:id/perfis/:pid": new Response(null, { status: 204 }),
      }),
    );
    await renderEm("/salao/pessoas");

    await userEvent.click(await screen.findByRole("button", { name: /^Bia/ }));
    const folha = await screen.findByRole("dialog", { name: "Bia" });
    expect(within(folha).getByText("Garçom: 3 ações permitidas")).toBeVisible();
    await userEvent.click(within(folha).getByRole("button", { name: "Garçom" }));
    await userEvent.click(within(folha).getByRole("button", { name: "Pronto" }));
    await userEvent.click(screen.getByRole("button", { name: /^Ana/ }));
    await userEvent.click(
      within(await screen.findByRole("dialog", { name: "Ana" })).getByRole("button", { name: "Garçom" }),
    );

    const escritas = chamadas.filter((c) => c.method === "PUT" || c.method === "DELETE");
    expect(escritas.map((c) => `${c.method} ${c.path}`)).toEqual([
      "PUT /contas/p2/perfis/g",
      "DELETE /contas/p1/perfis/g",
    ]);
  });

  it("says a person cannot change their own profile, without sending anything", async () => {
    const chamadas = stubRede(
      base({
        "POST /refresh": json(sessaoJson({ accessToken: tokenDe("p1") })),
      }),
    );
    await renderEm("/salao/pessoas");

    await userEvent.click(await screen.findByRole("button", { name: /^Ana/ }));
    const folha = await screen.findByRole("dialog", { name: "Ana" });
    expect(within(folha).getByText(/Você não pode mudar o seu próprio perfil/)).toBeVisible();
    expect(within(folha).queryByRole("button", { name: "Garçom" })).toBeNull();
    expect(chamadas.some((c) => c.method === "PUT")).toBe(false);
  });

  it("shows a 403 on assign as no permission and keeps the sheet open", async () => {
    stubRede(base({ "PUT /contas/:id/perfis/:pid": new Response(null, { status: 403 }) }));
    await renderEm("/salao/pessoas");

    await userEvent.click(await screen.findByRole("button", { name: /^Bia/ }));
    const folha = await screen.findByRole("dialog", { name: "Bia" });
    await userEvent.click(within(folha).getByRole("button", { name: "Caixa" }));

    expect(await within(folha).findByRole("alert")).toHaveTextContent("Você não tem permissão para isso.");
  });

  it("does not open the sheet for who cannot assign: the rows are plain text", async () => {
    stubRede(base({ "GET /sessao/permissoes": json({ permissoes: [...TODAS_AS_PERMISSOES, "perfil:ver"] }) }));
    await renderEm("/salao/pessoas");
    await screen.findByRole("list", { name: "Pessoas" });
    expect(screen.queryByRole("button", { name: /^Ana/ })).toBeNull();
  });
});
