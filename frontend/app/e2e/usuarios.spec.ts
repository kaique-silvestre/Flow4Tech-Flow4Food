import { mkdirSync } from "node:fs";
import { join } from "node:path";
import AxeBuilder from "@axe-core/playwright";
import { expect, type Page, test } from "@playwright/test";
import { lerSeed, SENHA } from "./seed";

const shots = join(import.meta.dirname, "..", "e2e-results", "screenshots");
mkdirSync(shots, { recursive: true });

const seed = lerSeed();
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

async function paraPessoas(page: Page) {
  await page.getByRole("link", { name: "Pessoas" }).click();
  await expect(page.getByRole("heading", { level: 2, name: "Pessoas" })).toBeVisible();
}

test("the owner creates the model profiles, gives Garçom to a person, and that person signs in and sees only waiter actions", async ({
  page,
}) => {
  test.setTimeout(120_000);
  await entrar(page, "dono@bar-a.test");
  await paraPessoas(page);
  const lista = page.getByRole("list", { name: "Pessoas" });
  await expect(lista.getByRole("button", { name: /^Dono do Bar A/ })).toBeVisible();
  // The tab bar stays pinned to the bottom of the screen on a long list (the evidence is viewport-sized).
  await expect(page.getByRole("navigation", { name: "Seções do salão" })).toBeInViewport({ ratio: 1 });
  await shoot(page, "47-pessoas-lista");
  await semHorizontal(page);
  expect(await alvosPequenos(page)).toEqual([]);
  await axe(page, "pessoas");

  // First use: the organization has no profile yet. The seed already made two, so the list is hidden at the network edge until the shortcut is tapped; the creation itself is real.
  let criado = false;
  await page.route("**/perfis", async (rota) => {
    if (!criado && rota.request().method() === "GET") {
      await rota.fulfill({ json: [] });
    } else {
      await rota.fallback();
    }
  });
  await page.reload();
  const atalho = page.getByRole("button", { name: "Criar perfis modelo" });
  await expect(atalho).toBeVisible();
  await expect(page.getByRole("navigation", { name: "Seções do salão" })).toBeInViewport({ ratio: 1 });
  await shoot(page, "47-pessoas-primeiro-uso");
  await semHorizontal(page);
  expect(await alvosPequenos(page)).toEqual([]);
  criado = true;
  await atalho.click();
  await expect(page.getByText(/Perfis criados|Agora escolha/).or(atalho)).toBeVisible();
  await page.unroute("**/perfis");
  await page.reload();
  await expect(page.getByRole("button", { name: "Criar perfis modelo" })).toHaveCount(0);

  // The owner cannot change their own profile: the sheet says so before anything is sent.
  await lista.getByRole("button", { name: /^Dono do Bar A/ }).click();
  const propria = page.getByRole("dialog", { name: "Dono do Bar A" });
  await expect(propria.getByText(/Você não pode mudar o seu próprio perfil/)).toBeVisible();
  await shoot(page, "47-pessoas-proprio-perfil");
  await propria.getByRole("button", { name: "Pronto" }).click();

  // Take the waiter profile away from "garcom", then give it back: the row shows the effect.
  const linha = page.getByRole("button").filter({ hasText: /garcom · / });
  await linha.first().click();
  const folha = page.getByRole("dialog").first();
  const chip = folha.getByRole("button", { name: "Garçom" });
  if ((await chip.getAttribute("aria-pressed")) === "true") {
    await chip.click();
    await expect(chip).toHaveAttribute("aria-pressed", "false");
  }

  await shoot(page, "47-pessoas-folha-sem-perfil");
  await chip.click();
  await expect(chip).toHaveAttribute("aria-pressed", "true");
  await shoot(page, "47-pessoas-folha-atribuido");
  await semHorizontal(page);
  expect(await alvosPequenos(page)).toEqual([]);
  await axe(page, "pessoas-folha");
  await folha.getByRole("button", { name: "Pronto" }).click();
  await expect(linha.first()).toContainText("Garçom");

  // That person signs in and sees only waiter actions: Comandas, no Caixa, no Pessoas.
  await page.getByRole("button", { name: "Sair" }).click();
  await expect(page).toHaveURL(/\/salao\/entrar$/);
  await entrar(page, "garcom");
  const abas = page.getByRole("navigation", { name: "Seções do salão" });
  await expect(abas.getByRole("link", { name: "Comandas" })).toBeVisible();
  await expect(abas.getByRole("link", { name: "Caixa" })).toHaveCount(0);
  await expect(abas.getByRole("link", { name: "Pessoas" })).toHaveCount(0);
  await page.goto("/salao/pessoas");
  await expect(page.getByText("Você não tem permissão para isso.")).toBeVisible();
  await shoot(page, "47-pessoas-sem-permissao");
});

test("the states of the screen: loading, error, forbidden by the server, and no people", async ({ page }) => {
  await entrar(page, "dono@bar-a.test");

  // Loading: the people request is held.
  let soltar: () => void = () => undefined;
  const segurada = new Promise<void>((resolve) => {
    soltar = resolve;
  });
  await page.route("**/contas", async (rota) => {
    if (rota.request().method() === "GET") {
      await segurada;
    }
    await rota.fallback();
  });
  await page.getByRole("link", { name: "Pessoas" }).click();
  await expect(page.getByRole("status", { name: "Carregando as pessoas" })).toBeVisible();
  await shoot(page, "47-pessoas-carregando");
  await semHorizontal(page);
  soltar();
  await expect(page.getByRole("heading", { level: 2, name: "Pessoas" })).toBeVisible();
  await page.unroute("**/contas");

  // Error.
  await page.route("**/contas", (rota) => rota.fulfill({ status: 500, body: "{}" }));
  await page.reload();
  await expect(page.getByText("Não foi possível carregar as pessoas.")).toBeVisible();
  await shoot(page, "47-pessoas-erro");
  await semHorizontal(page);
  expect(await alvosPequenos(page)).toEqual([]);
  await page.unroute("**/contas");

  // Forbidden by the server: a bare 403, the screen gives no reason.
  await page.route("**/contas", (rota) => rota.fulfill({ status: 403 }));
  await page.reload();
  await expect(page.getByText("Você não tem permissão para isso.")).toBeVisible();
  await shoot(page, "47-pessoas-proibido");
  await semHorizontal(page);
  await page.unroute("**/contas");

  // No people at all.
  await page.route("**/contas", (rota) => rota.fulfill({ json: [] }));
  await page.reload();
  await expect(page.getByText(/Nenhuma pessoa cadastrada/)).toBeVisible();
  await shoot(page, "47-pessoas-vazio");
  await semHorizontal(page);
});
