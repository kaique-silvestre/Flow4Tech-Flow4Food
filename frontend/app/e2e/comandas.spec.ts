import { mkdirSync } from "node:fs";
import { join } from "node:path";
import { expect, type Page, test } from "@playwright/test";
import { lerSeed, SENHA } from "./seed";

const shots = join(import.meta.dirname, "..", "e2e-results", "screenshots");
mkdirSync(shots, { recursive: true });

const seed = lerSeed();
test.skip(!process.env["E2E_API"] || seed === null, "needs the API running, the seed and E2E_API=1");

async function shoot(page: Page, name: string) {
  const size = page.viewportSize();
  await page.screenshot({ path: join(shots, `${name}-${size?.width ?? 0}x${size?.height ?? 0}.png`) });
}

async function entrarComoGarcom2(page: Page) {
  await page.goto("/salao/entrar");
  await page.getByLabel("Usuário ou e-mail").fill("garcom2");
  await page.getByLabel("Código da organização").fill(seed!.organizacaoA);
  await page.getByLabel("Senha").fill(SENHA);
  await page.getByRole("button", { name: "Entrar" }).click();
  await expect(page.getByRole("heading", { level: 2, name: "Minhas comandas" })).toBeVisible();
}

const teclado = (page: Page) => page.getByRole("group", { name: "Teclado numérico" });

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
      .map((el) => ({
        nome: el.textContent?.trim() || el.getAttribute("aria-label") || el.tagName,
        r: el.getBoundingClientRect(),
      }))
      .filter(({ r }) => r.width > 0 && (r.width < 44 || r.height < 44))
      .map(({ nome, r }) => `${nome} ${Math.round(r.width)}x${Math.round(r.height)}`),
  );
}

test("from the list to the comanda screen in under ten seconds of scripted interaction", async ({ page }) => {
  await entrarComoGarcom2(page);

  // Keypad path: Nova comanda, one key for the table, OK (and the confirmation when the table already has a comanda from an earlier run).
  const inicio = Date.now();
  await page.getByRole("button", { name: "Nova comanda" }).click();
  const folha = page.getByRole("dialog", { name: "Nova comanda" });
  await shoot(page, "11-nova-comanda-vazia");
  await teclado(page).getByRole("button", { name: "5", exact: true }).click();
  await expect(folha.getByLabel("Mesa 5")).toBeVisible();
  await shoot(page, "11-nova-comanda-teclado");
  await folha.getByRole("button", { name: "OK" }).click();
  const confirmar = page.getByRole("dialog", { name: /Mesa 5 já está ocupada/ });
  if (await confirmar.isVisible().catch(() => false)) {
    await confirmar.getByRole("button", { name: "Abrir outra comanda" }).click();
  }

  await expect(page).toHaveURL(/\/salao\/comanda\//);
  await expect(page.getByLabel("Total da conta")).toBeVisible();
  expect(Date.now() - inicio).toBeLessThan(10_000);
  await shoot(page, "11-comanda-aberta");
});

test("a table that already has a comanda asks before opening another and offers the existing one", async ({ page }) => {
  await entrarComoGarcom2(page);
  await page.getByRole("button", { name: "Nova comanda" }).click();
  await teclado(page).getByRole("button", { name: "6", exact: true }).click();
  await page.getByRole("dialog", { name: "Nova comanda" }).getByRole("button", { name: "OK" }).click();
  const primeira = page.getByRole("dialog", { name: /Mesa 6 já está ocupada/ });
  if (await primeira.isVisible().catch(() => false)) {
    await primeira.getByRole("button", { name: "Abrir outra comanda" }).click();
  }

  await expect(page).toHaveURL(/\/salao\/comanda\//);
  await page.getByRole("link", { name: "Comandas", exact: true }).first().click();
  await expect(page.getByRole("heading", { level: 2, name: "Minhas comandas" })).toBeVisible();

  await page.getByRole("button", { name: "Nova comanda" }).click();
  await teclado(page).getByRole("button", { name: "6", exact: true }).click();
  await page.getByRole("dialog", { name: "Nova comanda" }).getByRole("button", { name: "OK" }).click();

  const aviso = page.getByRole("dialog", { name: "Mesa 6 já está ocupada" });
  await expect(aviso).toBeVisible();
  await expect(
    aviso.getByRole("list", { name: "Comandas já abertas nesta mesa" }).getByRole("button").first(),
  ).toBeVisible();
  await shoot(page, "11-mesa-ocupada");
  await aviso.getByRole("button", { name: "Abrir outra comanda" }).click();
  await expect(page).toHaveURL(/\/salao\/comanda\//);
});

test("a comanda can be named, shows in the list and is found by search", async ({ page }) => {
  await entrarComoGarcom2(page);
  const nome = `Ana e Beto ${Date.now() % 100000}`;

  await page.getByRole("button", { name: "Nova comanda" }).click();
  await page.getByRole("dialog", { name: "Nova comanda" }).getByRole("button", { name: "Dar um nome" }).click();
  await shoot(page, "11-dar-um-nome");
  await page.getByLabel("Nome da comanda").fill(nome);
  await page.getByRole("dialog", { name: "Dar um nome" }).getByRole("button", { name: "OK" }).click();
  await expect(page).toHaveURL(/\/salao\/comanda\//);
  await expect(page.getByRole("heading", { level: 2, name: new RegExp(nome) })).toBeVisible();

  await page.getByRole("link", { name: "Comandas", exact: true }).first().click();
  await expect(page.getByRole("link", { name: new RegExp(nome) })).toBeVisible();
  await page.getByLabel("Buscar comanda").fill(nome.toLowerCase());
  await expect(page.getByRole("link", { name: new RegExp(nome) })).toBeVisible();
  await shoot(page, "11-lista");
  await semHorizontal(page);
  expect(await alvosPequenos(page)).toEqual([]);

  await page.getByLabel("Buscar comanda").fill("xyzxyz");
  await expect(page.getByText("Nada com “xyzxyz”. Tente o número da comanda.")).toBeVisible();
  await shoot(page, "11-busca-sem-resultado");
});

test("keeps the list and says so when the connection drops", async ({ page, context }) => {
  await entrarComoGarcom2(page);
  await expect(page.getByRole("link").filter({ hasText: /#\d+/ }).first()).toBeVisible();

  await context.setOffline(true);

  await expect(page.getByText("Sem conexão. A lista pode estar desatualizada.")).toBeVisible();
  await expect(page.getByRole("link").filter({ hasText: /#\d+/ }).first()).toBeVisible();
  await shoot(page, "11-lista-sem-conexao");
  await context.setOffline(false);
  await expect(page.getByText("Sem conexão. A lista pode estar desatualizada.")).toHaveCount(0);
});

test("the sheet has no horizontal scroll and every target is at least 44px", async ({ page }) => {
  await entrarComoGarcom2(page);
  await page.getByRole("button", { name: "Nova comanda" }).click();
  await expect(page.getByRole("dialog", { name: "Nova comanda" })).toBeVisible();

  await semHorizontal(page);
  expect(await alvosPequenos(page)).toEqual([]);
});
