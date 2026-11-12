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

async function abrirMapa(page: Page) {
  await page.getByRole("link", { name: "Mesas", exact: true }).click();
  await expect(page.getByRole("heading", { level: 2, name: "Mesas" })).toBeVisible();
  await expect(page.getByRole("heading", { level: 3, name: /Varanda/ })).toBeVisible();
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
      .map((el) => ({
        nome: el.textContent?.trim() || el.getAttribute("aria-label") || el.tagName,
        r: el.getBoundingClientRect(),
      }))
      .filter(({ r }) => r.width > 0 && (r.width < 44 || r.height < 44))
      .map(({ nome, r }) => `${nome} ${Math.round(r.width)}x${Math.round(r.height)}`),
  );
}

test("the map shows the areas, every status in words, big targets and no sideways scroll, in both themes", async ({
  page,
}) => {
  await entrarComoGarcom2(page);
  await abrirMapa(page);

  await expect(page.getByRole("heading", { level: 3, name: /Salão/ })).toBeVisible();
  await expect(page.getByRole("list", { name: "Salão" }).getByRole("listitem")).toHaveCount(3);
  await expect(page.getByRole("list", { name: "Varanda" }).getByRole("listitem")).toHaveCount(3);
  // A status is never only a color: every row carries its word.
  const palavras = page.locator(".mesa-row .f4-badge");
  await expect(palavras.first()).toBeVisible();
  for (const selo of await palavras.all()) {
    await expect(selo).toHaveText(/^(Livre|Ocupada|Aguardando conta)/);
  }

  await semHorizontal(page);
  expect(await alvosPequenos(page)).toEqual([]);
  for (const tema of ["light", "dark"] as const) {
    await page.emulateMedia({ colorScheme: tema });
    await shoot(page, `12-mapa-${tema}`);
  }

  await page.emulateMedia({ colorScheme: "light" });
});

test("a free table opens a comanda with one tap, then shows as occupied with its comanda", async ({ page }) => {
  await entrarComoGarcom2(page);
  await abrirMapa(page);

  const livre = page.getByRole("button", { name: /livre\. Abrir comanda/ }).first();
  test.skip(
    (await livre.count()) === 0,
    "every seeded table is already in use: reseed (docker compose down -v) to run this again",
  );
  const nome = (await livre.getAttribute("aria-label"))!.split(",")[0]!;
  await livre.click();
  await expect(page).toHaveURL(/\/salao\/comanda\//);
  await expect(page.getByLabel("Total da conta")).toBeVisible();

  await page.getByRole("link", { name: "Mesas", exact: true }).click();
  const ocupada = page.getByRole("button", { name: new RegExp(`^${nome}, ocupada`) });
  await expect(ocupada).toBeVisible();
  await ocupada.click();
  const folha = page.getByRole("dialog", { name: nome });
  await expect(folha).toBeVisible();
  await expect(folha.getByRole("list", { name: "Comandas desta mesa" }).getByRole("button").first()).toBeVisible();
  await expect(folha.getByRole("button", { name: "Abrir outra comanda" })).toBeVisible();
  await semHorizontal(page);
  await page.waitForTimeout(400);
  await shoot(page, "12-mesa-ocupada");
});

test("a comanda moves to another table and the map follows, offering the move only in the sheet of an occupied table", async ({
  page,
}) => {
  await entrarComoGarcom2(page);
  await abrirMapa(page);
  const livre = page.getByRole("button", { name: /livre\. Abrir comanda/ }).first();
  test.skip(
    (await page.getByRole("button", { name: /livre\. Abrir comanda/ }).count()) < 2,
    "needs two free seeded tables: reseed (docker compose down -v) to run this again",
  );
  const origem = (await livre.getAttribute("aria-label"))!.split(",")[0]!;
  await livre.click();
  await expect(page).toHaveURL(/\/salao\/comanda\//);
  await page.getByRole("link", { name: "Mesas", exact: true }).click();
  await page.getByRole("button", { name: new RegExp(`^${origem}, ocupada`) }).click();

  await page
    .getByRole("dialog", { name: origem })
    .getByRole("button", { name: /^Trocar de mesa a comanda/ })
    .click();
  const trocar = page.getByRole("dialog", { name: "Trocar de mesa" });
  await expect(trocar).toBeVisible();
  await expect(trocar.getByRole("button", { name: "Mover" })).toBeDisabled();
  const destino = trocar
    .getByRole("list", { name: "Mesas disponíveis" })
    .getByRole("button")
    .filter({ hasText: "Livre" })
    .first();
  await expect(destino).toBeVisible();
  await destino.click();
  await page.waitForTimeout(400);
  await shoot(page, "12-trocar-mesa");
  expect(await alvosPequenos(page)).toEqual([]);
  await trocar.getByRole("button", { name: /^Mover para/ }).click();

  await expect(page.getByRole("status").filter({ hasText: /mudou para/ })).toBeVisible();
  await expect(trocar).toBeHidden();
  await shoot(page, "12-mesa-trocada");
  await semHorizontal(page);
});
