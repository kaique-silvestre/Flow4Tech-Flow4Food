import { mkdirSync } from "node:fs";
import { join } from "node:path";
import AxeBuilder from "@axe-core/playwright";
import { type APIRequestContext, expect, type Page, test } from "@playwright/test";
import { lerSeed, SENHA } from "./seed";

const shots = join(import.meta.dirname, "..", "e2e-results", "screenshots");
mkdirSync(shots, { recursive: true });

const seed = lerSeed();
test.skip(!process.env["E2E_API"] || seed === null, "needs the API running, the seed and E2E_API=1");

async function shoot(page: Page, name: string) {
  // Every shot is taken at rest: transitions off, and an open sheet fully in place (never halfway through its slide).
  await page.addStyleTag({
    content: "*, *::before, *::after { transition: none !important; animation: none !important; }",
  });
  const folhas = page.getByRole("dialog");
  for (let i = 0; i < (await folhas.count()); i += 1) {
    await expect(folhas.nth(i)).toHaveCSS("transform", /none|matrix\(1, 0, 0, 1, 0, 0\)/);
  }

  const size = page.viewportSize();
  await page.screenshot({ path: join(shots, `${name}-${size?.width ?? 0}x${size?.height ?? 0}.png`) });
}

async function shootNosTemas(page: Page, name: string) {
  for (const tema of ["light", "dark"] as const) {
    await page.emulateMedia({ colorScheme: tema });
    await shoot(page, `${name}-${tema}`);
  }

  await page.emulateMedia({ colorScheme: "light" });
}

async function semHorizontal(page: Page) {
  const m = await page.evaluate(() => ({
    scroll: document.documentElement.scrollWidth,
    client: document.documentElement.clientWidth,
  }));
  expect(m.scroll).toBeLessThanOrEqual(m.client);
}

async function entrarComoDono(page: Page) {
  await page.goto("/salao/entrar");
  await page.getByLabel("Usuário ou e-mail").fill("dono@bar-a.test");
  await page.getByLabel("Senha").fill(SENHA);
  await page.getByRole("button", { name: "Entrar" }).click();
  await expect(page.getByRole("heading", { level: 1, name: "Salão" })).toBeVisible();
}

/** The waiter's side, straight through the API (another device): a comanda with 2 chopps (R$ 25,00; with the 10% fee R$ 27,50). */
async function comandaDoGarcom(request: APIRequestContext, baseURL: string, nome: string) {
  const headers = { "X-F4T-Csrf": "1", Origin: baseURL };
  const login = await request.post("/login", {
    headers,
    data: { organizacaoDoLogin: seed!.organizacaoA, usuario: "garcom2", senha: SENHA },
  });
  expect(login.ok()).toBeTruthy();
  const auth = { ...headers, Authorization: `Bearer ${((await login.json()) as { accessToken: string }).accessToken}` };
  const aberta = await request.post("/comandas", {
    headers: auth,
    data: { identificacao: nome, mesaId: null, pessoas: null },
  });
  expect(aberta.ok()).toBeTruthy();
  const comanda = (await aberta.json()) as { id: string; versao: number };
  const produtos = (await (await request.get("/cardapio/produtos", { headers: auth })).json()) as {
    id: string;
    codigo: string;
  }[];
  const chopp = produtos.find((p) => p.codigo === "CHOPP")!;
  const rodada = await request.post(`/comandas/${comanda.id}/rodadas`, {
    headers: auth,
    data: { rodadaId: crypto.randomUUID(), versao: comanda.versao, itens: [{ produtoId: chopp.id, quantidade: 2 }] },
  });
  expect(rodada.status()).toBe(201);
  return { id: comanda.id, versao: ((await rodada.json()) as { comandaVersao: number }).comandaVersao, auth };
}

async function abrirDaLista(page: Page, nome: string) {
  await page.getByRole("link", { name: new RegExp(nome) }).click();
  await expect(page.getByRole("heading", { level: 2, name: nome })).toBeVisible();
}

async function abrirMenu(page: Page, acao: string) {
  await page.getByRole("button", { name: "Mais" }).click();
  await page.getByRole("button", { name: acao, exact: true }).click();
}

async function semViolacoes(page: Page, tela: string) {
  for (const tema of ["light", "dark"] as const) {
    await page.emulateMedia({ colorScheme: tema });
    const r = await new AxeBuilder({ page }).withTags(["wcag2a", "wcag2aa", "wcag21a", "wcag21aa"]).analyze();
    expect(
      r.violations.map((v) => ({ regra: v.id, alvos: v.nodes.map((n) => n.target.join(" ")) })),
      `${tela}/${tema}`,
    ).toEqual([]);
  }

  await page.emulateMedia({ colorScheme: "light" });
}

async function digitar(page: Page, texto: string) {
  const folha = page.getByRole("dialog");
  for (const d of texto) {
    await folha.getByRole("button", { name: d, exact: true }).click();
  }
}

test("discount, then courtesy: the owner sees the new total before confirming, and the bill follows", async ({
  page,
  request,
  baseURL,
}) => {
  const nome = `Desconto ${Date.now() % 100000}`;
  await comandaDoGarcom(request, baseURL!, nome);
  await entrarComoDono(page);
  await abrirDaLista(page, nome);
  await expect(page.getByLabel("Total da conta")).toContainText("27,50");

  await page.getByRole("button", { name: "Desconto em Chopp 300ml" }).click();
  const folha = page.getByRole("dialog", { name: "Desconto, cortesia ou brinde" });
  await digitar(page, "10");
  await folha.getByRole("button", { name: "Cliente frequente" }).click();
  await expect(folha.getByLabel("Prévia da conta")).toContainText("24,75");
  await semHorizontal(page);
  await shootNosTemas(page, "desconto-item");
  await semViolacoes(page, "desconto-item");

  // Loading: the answer is held back, the sheet shows the request in flight.
  await page.route("**/comandas/*/descontos", async (rota) => {
    await new Promise((r) => setTimeout(r, 2500));
    await rota.continue();
  });
  await folha.getByRole("button", { name: "Aplicar desconto" }).click();
  await expect(folha.getByRole("button", { name: "Aplicar desconto" })).toBeDisabled();
  await shootNosTemas(page, "desconto-carregando");
  await expect(folha).toBeHidden();
  await page.unroute("**/comandas/*/descontos");
  await expect(page.getByLabel("Total da conta")).toContainText("24,75");
  await expect(page.getByLabel("Desconto da conta")).toContainText("2,50");
  await shootNosTemas(page, "desconto-na-conta");

  // Courtesy on the same item.
  await page.getByRole("button", { name: "Desconto em Chopp 300ml" }).click();
  await folha.getByRole("button", { name: "Cortesia", exact: true }).click();
  await folha.getByRole("button", { name: "Aniversário" }).click();
  await expect(folha.getByLabel("Prévia da conta")).toContainText("0,00");
  await shootNosTemas(page, "cortesia-item");
  await folha.getByRole("button", { name: "Dar cortesia" }).click();
  await expect(folha).toBeHidden();
  const linha = page.getByRole("list", { name: "Itens da conta" }).getByRole("listitem").first();
  await expect(linha).toContainText("Aniversário");
  await expect(page.getByLabel("Total da conta")).toContainText("0,00");
  await semHorizontal(page);
  await shootNosTemas(page, "cortesia-na-conta");
});

test("a decimal percent (7,5%) is typed with the comma, previewed, and the bill takes the same value", async ({
  page,
  request,
  baseURL,
}) => {
  const nome = `DescDec ${Date.now() % 100000}`;
  await comandaDoGarcom(request, baseURL!, nome);
  await entrarComoDono(page);
  await abrirDaLista(page, nome);

  await page.getByRole("button", { name: "Desconto em Chopp 300ml" }).click();
  const folha = page.getByRole("dialog", { name: "Desconto, cortesia ou brinde" });
  await digitar(page, "7");
  await folha.getByRole("button", { name: "Vírgula" }).click();
  await digitar(page, "555");
  await expect(folha.getByLabel("Desconto digitado")).toContainText("7,55%");
  await folha.getByRole("button", { name: "Apagar" }).click();
  await expect(folha.getByLabel("Desconto digitado")).toContainText("7,5%");
  await folha.getByRole("button", { name: "Cliente frequente" }).click();
  // 7,5% of R$ 25,00 = R$ 1,875 -> R$ 1,88; the fee (10%) on R$ 23,12 -> R$ 2,31; total R$ 25,43.
  await expect(folha.getByLabel("Prévia da conta")).toContainText("1,88");
  await expect(folha.getByLabel("Prévia da conta")).toContainText("25,43");
  await expect(folha.getByRole("button", { name: "Aplicar desconto" })).toBeInViewport();
  await semHorizontal(page);
  await shootNosTemas(page, "desconto-decimal");
  await semViolacoes(page, "desconto-decimal");
  await folha.getByRole("button", { name: "Aplicar desconto" }).click();
  await expect(folha).toBeHidden();
  await expect(page.getByLabel("Desconto da conta")).toContainText("1,88");
  await expect(page.getByLabel("Total da conta")).toContainText("25,43");
});

test("a failed request is told, keeps the sheet and the typed value, and the retry goes through", async ({
  page,
  request,
  baseURL,
}) => {
  const nome = `DescErro ${Date.now() % 100000}`;
  await comandaDoGarcom(request, baseURL!, nome);
  await entrarComoDono(page);
  await abrirDaLista(page, nome);

  await abrirMenu(page, "Desconto na comanda");
  const folha = page.getByRole("dialog", { name: "Desconto na comanda" });
  await folha.getByRole("button", { name: "Valor" }).click();
  await digitar(page, "500");
  await folha.getByRole("button", { name: "Erro de atendimento" }).click();
  await expect(folha.getByLabel("Prévia da conta")).toContainText("22,00");
  await page.route("**/comandas/*/descontos", (rota) => rota.fulfill({ status: 500, body: "" }));
  await folha.getByRole("button", { name: "Aplicar desconto" }).click();
  await expect(folha.getByText("Não foi possível aplicar. Tente de novo.")).toBeVisible();
  await expect(folha.getByLabel("Desconto digitado")).toContainText("5,00");
  await expect(folha.getByText("Não foi possível aplicar. Tente de novo.")).toBeInViewport();
  await expect(folha.getByLabel("Prévia da conta")).toBeInViewport();
  await shootNosTemas(page, "desconto-erro");
  await page.unroute("**/comandas/*/descontos");
  await folha.getByRole("button", { name: "Aplicar desconto" }).click();
  await expect(folha).toBeHidden();
  await expect(page.getByLabel("Total da conta")).toContainText("22,00");
});

test("the offline sheet explains that discount needs a connection and keeps nothing in the queue", async ({
  page,
  request,
  baseURL,
  context,
}) => {
  const nome = `DescOff ${Date.now() % 100000}`;
  await comandaDoGarcom(request, baseURL!, nome);
  await entrarComoDono(page);
  await abrirDaLista(page, nome);
  await context.setOffline(true);

  await page.getByRole("button", { name: "Desconto em Chopp 300ml" }).click();
  const folha = page.getByRole("dialog", { name: "Desconto, cortesia ou brinde" });
  await expect(folha.getByText(/precisam do servidor e não ficam na fila/)).toBeVisible();
  await expect(folha.getByRole("button", { name: "Aplicar desconto" })).toBeDisabled();
  await shootNosTemas(page, "desconto-sem-conexao");
  await context.setOffline(false);
});

async function entrarComoGarcom(page: Page) {
  await page.goto("/salao/entrar");
  await page.getByLabel("Usuário ou e-mail").fill("garcom2");
  await page.getByLabel("Código da organização").fill(seed!.organizacaoA);
  await page.getByLabel("Senha").fill(SENHA);
  await page.getByRole("button", { name: "Entrar" }).click();
  await expect(page.getByRole("heading", { level: 1, name: "Salão" })).toBeVisible();
}

test("a waiter with no discount rule is offered neither discount, courtesy nor gift, not even in the menu", async ({
  page,
  request,
  baseURL,
}) => {
  const nome = `DescSemRegra ${Date.now() % 100000}`;
  await comandaDoGarcom(request, baseURL!, nome);
  await entrarComoGarcom(page);
  await abrirDaLista(page, nome);

  await expect(page.getByText("Chopp 300ml", { exact: true })).toBeVisible();
  await expect(page.getByRole("button", { name: "Desconto em Chopp 300ml" })).toHaveCount(0);
  // The waiter has other menu actions (rename, history...), but the discount of the comanda is not among them.
  await page.getByRole("button", { name: "Mais" }).click();
  await expect(page.getByRole("list", { name: "Ações da comanda" })).toBeVisible();
  await expect(page.getByRole("button", { name: "Desconto na comanda" })).toHaveCount(0);
});

test("the server's 403 stays the last defence: told 'sem permissão para este valor', with no limit named", async ({
  page,
  request,
  baseURL,
}) => {
  const nome = `DescNegado ${Date.now() % 100000}`;
  await comandaDoGarcom(request, baseURL!, nome);
  // The screen is made to believe the waiter has a rule (as if the rule had been removed meanwhile): the server still refuses.
  await page.route("**/sessao/regras-de-desconto", (rota) =>
    rota.fulfill({ json: { desconto: true, cortesia: true, brinde: true } }),
  );
  await entrarComoGarcom(page);
  await abrirDaLista(page, nome);

  await page.getByRole("button", { name: "Desconto em Chopp 300ml" }).click();
  const folha = page.getByRole("dialog", { name: "Desconto, cortesia ou brinde" });
  await digitar(page, "50");
  await folha.getByRole("button", { name: "Reclamação do cliente" }).click();
  await folha.getByRole("button", { name: "Aplicar desconto" }).click();
  await expect(folha.getByText("Sem permissão para este valor.")).toBeVisible();
  await expect(folha).not.toContainText(/limite/i);
  await expect(folha.getByText("Sem permissão para este valor.")).toBeInViewport();
  await expect(folha.getByLabel("Prévia da conta")).toBeInViewport();
  await expect(folha.getByRole("button", { name: "Aplicar desconto" })).toBeInViewport();
  await expect(page.getByLabel("Total da conta")).toContainText("27,50");
  await semHorizontal(page);
  await shootNosTemas(page, "desconto-negado");
  await semViolacoes(page, "desconto-negado");
});

test("the close-bill sheet lists the courtesy and the gift as such, and the discount", async ({
  page,
  request,
  baseURL,
}) => {
  const nome = `DescConta ${Date.now() % 100000}`;
  const c = await comandaDoGarcom(request, baseURL!, nome);
  // The second item is a product of this test (no option group), so what the menu already holds, from the seed or from another spec, never matters.
  const dono = await request.post("/login", {
    headers: { "X-F4T-Csrf": "1", Origin: baseURL! },
    data: { email: "dono@bar-a.test", senha: SENHA },
  });
  expect(dono.ok()).toBeTruthy();
  const donoAuth = {
    "X-F4T-Csrf": "1",
    Origin: baseURL!,
    Authorization: `Bearer ${((await dono.json()) as { accessToken: string }).accessToken}`,
  };
  const categorias = (await (await request.get("/cardapio/categorias", { headers: donoAuth })).json()) as {
    id: string;
  }[];
  const sufixo = `${Date.now() % 1_000_000}`;
  const criado = await request.post("/cardapio/produtos", {
    headers: donoAuth,
    data: {
      codigo: `DC${sufixo}`,
      nome: `Porção Desc ${sufixo}`,
      precoCentavos: 1800,
      categoriaId: categorias[0]!.id,
      ativo: true,
      indisponivel: false,
    },
  });
  expect(criado.ok()).toBeTruthy();
  const outro = { ...((await criado.json()) as { id: string }), nome: `Porção Desc ${sufixo}` };
  const rodada = await request.post(`/comandas/${c.id}/rodadas`, {
    headers: c.auth,
    data: { rodadaId: crypto.randomUUID(), versao: c.versao, itens: [{ produtoId: outro.id, quantidade: 1 }] },
  });
  expect(rodada.status()).toBe(201);
  await entrarComoDono(page);
  await abrirDaLista(page, nome);

  const folha = page.getByRole("dialog", { name: "Desconto, cortesia ou brinde" });
  await page.getByRole("button", { name: "Desconto em Chopp 300ml" }).click();
  await folha.getByRole("button", { name: "Cortesia", exact: true }).click();
  await folha.getByRole("button", { name: "Aniversário" }).click();
  await folha.getByRole("button", { name: "Dar cortesia" }).click();
  await expect(folha).toBeHidden();
  await page.getByRole("button", { name: `Desconto em ${outro.nome}` }).click();
  await folha.getByRole("button", { name: "Brinde", exact: true }).click();
  await folha.getByRole("button", { name: "Decisão da gerência" }).click();
  await folha.getByRole("button", { name: "Dar brinde" }).click();
  await expect(folha).toBeHidden();
  await shootNosTemas(page, "cortesia-brinde-na-conta");
  await semHorizontal(page);

  await page.getByRole("button", { name: "Fechar conta" }).click();
  const fechar = page.getByRole("dialog", { name: /Fechar/ });
  await expect(fechar.getByLabel("Conta")).toContainText("Cortesia");
  await expect(fechar.getByLabel("Conta")).toContainText("Brinde");
  await shootNosTemas(page, "fechar-conta-cortesia-brinde");
  await semViolacoes(page, "fechar-conta-cortesia");
});
