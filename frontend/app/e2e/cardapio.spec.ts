import { mkdirSync } from "node:fs";
import { join } from "node:path";
import AxeBuilder from "@axe-core/playwright";
import { type APIRequestContext, expect, type Page, test } from "@playwright/test";
import { lerSeed, SENHA } from "./seed";

const shots = join(import.meta.dirname, "..", "e2e-results", "screenshots");
mkdirSync(shots, { recursive: true });

const seed = lerSeed();
test.use({
  locale: "pt-BR",
  launchOptions: {
    args: ["--no-sandbox", "--lang=pt-BR"],
    ...(process.env["PW_CHROMIUM"] ? { executablePath: process.env["PW_CHROMIUM"] } : {}),
  },
});
test.skip(!process.env["E2E_API"] || seed === null, "needs the API running, the seed and E2E_API=1");

/** Every shot is taken at rest: transitions off, an open sheet fully in place, both themes. */
async function shoot(page: Page, name: string) {
  await page.addStyleTag({
    content: "*, *::before, *::after { transition: none !important; animation: none !important; }",
  });
  const folhas = page.getByRole("dialog");
  for (let i = 0; i < (await folhas.count()); i += 1) {
    await expect(folhas.nth(i)).toHaveCSS("transform", /none|matrix\(1, 0, 0, 1, 0, 0\)/);
  }

  const size = page.viewportSize();
  for (const tema of ["light", "dark"] as const) {
    await page.emulateMedia({ colorScheme: tema });
    await page.screenshot({ path: join(shots, `${name}-${tema}-${size?.width ?? 0}x${size?.height ?? 0}.png`) });
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

async function alvosPequenos(page: Page) {
  return page.evaluate(() =>
    [...document.querySelectorAll<HTMLElement>("a, button, input, select, textarea")]
      .filter((el) => el.closest("[aria-hidden=true]") === null)
      .map((el) => ({
        nome: el.textContent?.trim() || el.getAttribute("aria-label") || el.tagName,
        r: el.getBoundingClientRect(),
      }))
      .filter(({ r }) => r.width > 0 && (r.width < 44 || r.height < 44))
      .map(({ nome, r }) => `${nome} ${Math.round(r.width)}x${Math.round(r.height)}`),
  );
}

async function axe(page: Page, tela: string) {
  for (const tema of ["light", "dark"] as const) {
    await page.emulateMedia({ colorScheme: tema });
    await page.addStyleTag({
      content: "*, *::before, *::after { transition: none !important; animation: none !important; }",
    });
    const r = await new AxeBuilder({ page }).withTags(["wcag2a", "wcag2aa", "wcag21a", "wcag21aa"]).analyze();
    expect(
      r.violations.map((v) => `${v.id}: ${v.nodes.map((n) => n.target.join(" ")).join(", ")}`),
      `${tela}/${tema}`,
    ).toEqual([]);
  }

  await page.emulateMedia({ colorScheme: "light" });
}

async function entrar(page: Page, usuario: string) {
  await page.goto("/salao/entrar");
  await page.getByLabel("Usuário ou e-mail").fill(usuario);
  if (!usuario.includes("@")) {
    await page.getByLabel("Código da organização").fill(seed!.organizacaoA);
  }

  await page.getByLabel("Senha").fill(SENHA);
  await page.getByRole("button", { name: "Entrar" }).click();
  await expect(page.getByRole("heading", { level: 1, name: "Salão" })).toBeVisible();
}

async function paraCardapio(page: Page) {
  await page.getByRole("link", { name: "Cardápio" }).click();
  await expect(page.getByRole("heading", { level: 2, name: "Cardápio" })).toBeVisible();
  await expect(page.getByRole("switch", { name: "Acabou: Chopp 300ml" })).toBeVisible();
}

/** A comanda opened from another device by the waiter, so the waiter's grid can be looked at in the browser. */
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
}

test("the owner marks a product as 'acabou' on the phone and the waiter's grid shows it, then everything is put back", async ({
  page,
  request,
  baseURL,
}) => {
  test.setTimeout(120_000);
  const nome = `Cardapio ${Date.now() % 100000}`;
  await comandaDoGarcom(request, baseURL!, nome);

  await entrar(page, "dono@bar-a.test");
  await paraCardapio(page);
  // The tab bar stays pinned to the bottom of the screen on a long list.
  await expect(page.getByRole("navigation", { name: "Seções do salão" })).toBeInViewport({ ratio: 1 });
  await shoot(page, "49-cardapio-lista");
  await semHorizontal(page);
  expect(await alvosPequenos(page)).toEqual([]);
  await axe(page, "cardapio");

  // One tap: 'acabou'.
  const acabou = page.getByRole("switch", { name: "Acabou: Chopp 300ml" });
  await acabou.click();
  await expect(acabou).toHaveAttribute("aria-checked", "true");
  await expect(page.getByRole("list", { name: "Bebidas" }).getByText("Acabou").first()).toBeVisible();
  await shoot(page, "49-cardapio-acabou");

  // Search narrows the list.
  await page.getByRole("searchbox", { name: "Buscar produto" }).fill("batata");
  await expect(page.getByRole("switch", { name: "Acabou: Porção de batata" })).toBeVisible();
  await expect(page.getByRole("switch", { name: "Acabou: Chopp 300ml" })).toHaveCount(0);
  await shoot(page, "49-cardapio-busca");
  await page.getByRole("searchbox", { name: "Buscar produto" }).fill("zzzz");
  await expect(page.getByText(/Nada com “zzzz”/)).toBeVisible();
  await shoot(page, "49-cardapio-busca-vazia");
  await page.getByRole("searchbox", { name: "Buscar produto" }).fill("");

  try {
    // The waiter signs in: the product that ran out is marked in the grid and cannot be added.
    await page.getByRole("button", { name: "Sair" }).click();
    await expect(page).toHaveURL(/\/salao\/entrar$/);
    await entrar(page, "garcom2");
    await page.getByText(nome).first().click();
    await expect(page.getByRole("heading", { level: 2, name: nome })).toBeVisible();
    await page.getByRole("searchbox", { name: "Buscar produto" }).fill("chopp");
    await expect(page.getByText("Chopp 300ml")).toBeVisible();
    await expect(page.getByText("Acabou")).toBeVisible();
    await expect(page.getByRole("button", { name: "Adicionar Chopp 300ml" })).toHaveCount(0);
    await page.getByRole("searchbox", { name: "Buscar produto" }).fill("refri");
    await expect(page.getByRole("button", { name: "Adicionar Refrigerante lata" })).toBeVisible();
    await page.getByRole("searchbox", { name: "Buscar produto" }).fill("chopp");
    await shoot(page, "49-grade-do-garcom-acabou");
    await semHorizontal(page);
    await axe(page, "grade-acabou");
  } finally {
    // Put it back, whatever happened above.
    await page.getByRole("button", { name: "Sair" }).click();
    await expect(page).toHaveURL(/\/salao\/entrar$/);
    await entrar(page, "dono@bar-a.test");
    await paraCardapio(page);
    const de_novo = page.getByRole("switch", { name: "Acabou: Chopp 300ml" });
    if ((await de_novo.getAttribute("aria-checked")) === "true") {
      await de_novo.click();
    }

    await expect(de_novo).toHaveAttribute("aria-checked", "false");
  }

  // Deactivate and reactivate: the row says so in words.
  const ativo = page.getByRole("switch", { name: "Ativo: Água sem gás" });
  await ativo.click();
  await expect(ativo).toHaveAttribute("aria-checked", "false");
  await expect(page.getByText("Inativo")).toBeVisible();
  await shoot(page, "49-cardapio-inativo");
  await ativo.click();
  await expect(ativo).toHaveAttribute("aria-checked", "true");
});

test("the owner changes a price and sets a promotion in the sheet, and the sheet refuses a bad promotion", async ({
  page,
}) => {
  test.setTimeout(120_000);
  await entrar(page, "dono@bar-a.test");
  await paraCardapio(page);

  await page.getByRole("button", { name: "Preço de Refrigerante lata" }).click();
  const folha = page.getByRole("dialog", { name: "Preço de Refrigerante lata" });
  await expect(folha.getByLabel("Preço normal")).toHaveValue(/7,00/);
  await shoot(page, "49-preco-folha");
  await semHorizontal(page);
  expect(await alvosPequenos(page)).toEqual([]);
  await axe(page, "preco-folha");

  // A bad promotion is explained before anything is sent.
  await folha.getByLabel("Preço promocional").fill("900");
  await folha.getByRole("button", { name: "Salvar preço" }).click();
  await expect(folha.getByText(/precisa ser menor que o preço normal/)).toBeVisible();
  await shoot(page, "49-preco-folha-erro");

  // A real promotion, then the price put back.
  await folha.getByLabel("Preço promocional").fill("500");
  await folha.getByLabel("Promoção começa").fill("01/01/2020 10:00");
  await folha.getByLabel("Promoção termina").fill("01/01/2099 10:00");
  await folha.getByRole("button", { name: "Salvar preço" }).click();
  await expect(page.getByRole("dialog")).toHaveCount(0);
  const linha = page.getByRole("listitem").filter({ hasText: "Refrigerante lata" });
  await expect(linha.getByText("Promoção")).toBeVisible();
  await expect(linha).toContainText(/R\$\s5,00/);
  await shoot(page, "49-cardapio-promocao");

  await page.getByRole("button", { name: "Preço de Refrigerante lata" }).click();
  const volta = page.getByRole("dialog", { name: "Preço de Refrigerante lata" });
  await volta.getByLabel("Preço promocional").fill("");
  await volta.getByRole("button", { name: "Salvar preço" }).click();
  await expect(page.getByRole("dialog")).toHaveCount(0);
  await expect(linha.getByText("Promoção")).toHaveCount(0);
  await expect(linha).toContainText(/R\$\s7,00/);
});

test("the states of the screen: loading, error, forbidden by the server, empty, price denied, conflict, no connection", async ({
  page,
}) => {
  test.setTimeout(120_000);
  await entrar(page, "dono@bar-a.test");

  // Loading: the products request is held.
  let soltar: () => void = () => undefined;
  const segurada = new Promise<void>((resolve) => {
    soltar = resolve;
  });
  await page.route("**/cardapio/produtos/gestao", async (rota) => {
    await segurada;
    await rota.fallback();
  });
  await page.getByRole("link", { name: "Cardápio" }).click();
  await expect(page.getByRole("status", { name: "Carregando o cardápio" })).toBeVisible();
  await shoot(page, "49-cardapio-carregando");
  await semHorizontal(page);
  soltar();
  await expect(page.getByRole("heading", { level: 2, name: "Cardápio" })).toBeVisible();
  await page.unroute("**/cardapio/produtos/gestao");

  // Error.
  await page.route("**/cardapio/produtos/gestao", (rota) => rota.fulfill({ status: 500, body: "{}" }));
  await page.reload();
  await expect(page.getByText("Não foi possível carregar o cardápio.")).toBeVisible();
  await shoot(page, "49-cardapio-erro");
  await semHorizontal(page);
  expect(await alvosPequenos(page)).toEqual([]);
  await page.unroute("**/cardapio/produtos/gestao");

  // Forbidden by the server: a bare 403, no reason.
  await page.route("**/cardapio/produtos/gestao", (rota) => rota.fulfill({ status: 403 }));
  await page.reload();
  await expect(page.getByText("Você não tem permissão para isso.")).toBeVisible();
  await shoot(page, "49-cardapio-proibido");
  await semHorizontal(page);
  await page.unroute("**/cardapio/produtos/gestao");

  // Empty menu.
  await page.route("**/cardapio/produtos/gestao", (rota) => rota.fulfill({ json: [] }));
  await page.reload();
  await expect(page.getByText(/Nenhum produto no cardápio ainda/)).toBeVisible();
  await shoot(page, "49-cardapio-vazio");
  await semHorizontal(page);
  await page.unroute("**/cardapio/produtos/gestao");

  // Price above the limit: the server answers a bare 403 and the sheet stays open with the generic message.
  await page.reload();
  await expect(page.getByRole("switch", { name: "Acabou: Chopp 300ml" })).toBeVisible();
  await page.route("**/cardapio/produtos/*", async (rota) => {
    if (rota.request().method() === "PUT") {
      await rota.fulfill({ status: 403 });
    } else {
      await rota.fallback();
    }
  });
  await page.getByRole("button", { name: "Preço de Chopp 300ml" }).click();
  const folha = page.getByRole("dialog", { name: "Preço de Chopp 300ml" });
  await folha.getByLabel("Preço normal").fill("9900");
  await folha.getByRole("button", { name: "Salvar preço" }).click();
  await expect(folha.getByRole("alert")).toHaveText("Você não tem permissão para isso.");
  await shoot(page, "49-preco-negado");
  await semHorizontal(page);
  await folha.getByRole("button", { name: "Cancelar" }).click();

  // A switch refused by the server (403) and one in conflict (409) say so on the row; the switch stays where it was.
  await page.getByRole("switch", { name: "Acabou: Chopp 300ml" }).click();
  await expect(page.getByText("Você não tem permissão para isso.")).toBeVisible();
  await expect(page.getByRole("switch", { name: "Acabou: Chopp 300ml" })).toHaveAttribute("aria-checked", "false");
  await shoot(page, "49-cardapio-linha-negada");
  await page.unroute("**/cardapio/produtos/*");

  await page.route("**/cardapio/produtos/*", async (rota) => {
    if (rota.request().method() === "PUT") {
      await rota.fulfill({ status: 409, json: { codigo: "versao-desatualizada" } });
    } else {
      await rota.fallback();
    }
  });
  await page.getByRole("switch", { name: "Acabou: Água sem gás" }).click();
  await expect(page.getByText(/foi alterado em outro aparelho/)).toBeVisible();
  await shoot(page, "49-cardapio-conflito");
  await semHorizontal(page);
  await page.unroute("**/cardapio/produtos/*");

  // No connection: the write cannot go out. Start from a clean screen so the shot shows only this message.
  await page.reload();
  await expect(page.getByRole("switch", { name: "Acabou: Refrigerante lata" })).toBeVisible();
  await page.route("**/cardapio/produtos/*", async (rota) => {
    if (rota.request().method() === "PUT") {
      await rota.abort("connectionfailed");
    } else {
      await rota.fallback();
    }
  });
  await page.getByRole("switch", { name: "Acabou: Refrigerante lata" }).click();
  const aviso = page.getByText("Sem conexão. Tente de novo.");
  await expect(aviso).toBeVisible();
  await aviso.evaluate((el) => el.scrollIntoView({ block: "center" }));
  await shoot(page, "49-cardapio-sem-conexao");
  await page.unroute("**/cardapio/produtos/*");
});

test("a person without the right to manage products has no tab and the direct route says no", async ({ page }) => {
  await entrar(page, "garcom2");
  const abas = page.getByRole("navigation", { name: "Seções do salão" });
  await expect(abas.getByRole("link", { name: "Comandas" })).toBeVisible();
  await expect(abas.getByRole("link", { name: "Cardápio" })).toHaveCount(0);
  await page.goto("/salao/cardapio");
  await expect(page.getByText("Você não tem permissão para isso.")).toBeVisible();
  await shoot(page, "49-cardapio-sem-permissao");
});
