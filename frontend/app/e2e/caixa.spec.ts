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

async function entrarComo(page: Page, usuario: string) {
  await page.goto("/salao/entrar");
  await page.getByLabel("Usuário ou e-mail").fill(usuario);
  await page.getByLabel("Código da organização").fill(seed!.organizacaoA);
  await page.getByLabel("Senha").fill(SENHA);
  await page.getByRole("button", { name: "Entrar" }).click();
  await expect(page.getByRole("heading", { level: 1, name: "Salão" })).toBeVisible();
}

async function contar(page: Page, centavos: string) {
  await page.getByRole("button", { name: "Fechar caixa" }).click();
  const folha = page.getByRole("dialog", { name: "Fechar caixa" });
  await folha.getByLabel("Dinheiro contado").fill(centavos);
  return folha;
}

/** A shift left open by an earlier run is closed first, so the flow always starts from the closed state. */
async function comecarFechado(page: Page) {
  await page.getByRole("link", { name: "Caixa" }).click();
  const aberto = page.getByRole("heading", { level: 2, name: "Caixa aberto" });
  const fechado = page.getByText("Caixa fechado. Abra o caixa para receber.");
  await expect(aberto.or(fechado)).toBeVisible();
  if (await aberto.isVisible()) {
    const folha = await contar(page, "0");
    await folha.getByRole("button", { name: "Fechar caixa" }).click();
    await page.getByRole("button", { name: "Concluir" }).click();
  }

  await expect(fechado).toBeVisible();
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

test("the cashier opens the register with a float, sees the shift and closes it with the difference in words", async ({
  page,
}) => {
  await entrarComo(page, "caixa");
  await comecarFechado(page);
  await shoot(page, "10-caixa-fechado");
  await semHorizontal(page);
  expect(await alvosPequenos(page)).toEqual([]);

  // Open with R$ 100,00 of float: the shift shows it, the expected cash is the float alone (nothing was received).
  await page.getByLabel("Fundo de troco").fill("10000");
  await expect(page.getByLabel("Fundo de troco")).toHaveValue(/100,00/);
  await page.getByRole("button", { name: "Abrir caixa" }).click();
  await expect(page.getByRole("heading", { level: 2, name: "Caixa aberto" })).toBeVisible();
  await expect(page.getByText("Esperado na gaveta").locator("..")).toContainText("100,00");
  await expect(page.getByText("O fundo não é venda")).toBeVisible();
  await shoot(page, "10-caixa-aberto");
  await semHorizontal(page);
  expect(await alvosPequenos(page)).toEqual([]);

  // A shortage: counted R$ 95,00 against R$ 100,00.
  let folha = await contar(page, "9500");
  await shoot(page, "10-caixa-contagem");
  await folha.getByRole("button", { name: "Fechar caixa" }).click();
  await expect(page.getByText("Falta R$ 5,00")).toBeVisible();
  await shoot(page, "10-caixa-resultado-falta");
  await page.getByRole("button", { name: "Concluir" }).click();
  await expect(page.getByText("Caixa fechado. Abra o caixa para receber.")).toBeVisible();

  // A surplus, then an exact count (a zero float is a real float).
  await page.getByLabel("Fundo de troco").fill("5000");
  await page.getByRole("button", { name: "Abrir caixa" }).click();
  folha = await contar(page, "6200");
  await folha.getByRole("button", { name: "Fechar caixa" }).click();
  await expect(page.getByText("Sobra R$ 12,00")).toBeVisible();
  await shoot(page, "10-caixa-resultado-sobra");
  await page.getByRole("button", { name: "Concluir" }).click();

  await page.getByLabel("Fundo de troco").fill("0");
  await page.getByRole("button", { name: "Abrir caixa" }).click();
  folha = await contar(page, "0");
  await folha.getByRole("button", { name: "Fechar caixa" }).click();
  await expect(page.getByText("Confere", { exact: true })).toBeVisible();
  await shoot(page, "10-caixa-resultado-confere");
  await page.getByRole("button", { name: "Concluir" }).click();
});

test("a second open from a stale screen is refused by the server and shown as an error the user can act on", async ({
  page,
}) => {
  await entrarComo(page, "caixa");
  await comecarFechado(page);
  await page.getByLabel("Fundo de troco").fill("1000");
  await page.getByRole("button", { name: "Abrir caixa" }).click();
  await expect(page.getByRole("heading", { level: 2, name: "Caixa aberto" })).toBeVisible();

  // The screen of another device still shows the register closed: the read answers "no shift" once, the open is real.
  await page.route(
    "**/caixa/turnos/atual",
    (route) => route.fulfill({ status: 404, json: { codigo: "sem-turno-aberto" } }),
    { times: 1 },
  );
  await page.getByRole("link", { name: "Comandas" }).click();
  await page.getByRole("link", { name: "Caixa" }).click();
  await expect(page.getByText("Caixa fechado. Abra o caixa para receber.")).toBeVisible();
  await page.getByLabel("Fundo de troco").fill("1000");
  await page.getByRole("button", { name: "Abrir caixa" }).click();

  await expect(page.getByRole("alert").filter({ hasText: "Você já tem um caixa aberto nesta unidade." })).toBeVisible();
  await shoot(page, "10-caixa-erro-ja-aberto");
  await page.getByRole("button", { name: "Atualizar" }).click();
  await expect(page.getByRole("heading", { level: 2, name: "Caixa aberto" })).toBeVisible();

  const folha = await contar(page, "1000");
  await folha.getByRole("button", { name: "Fechar caixa" }).click();
  await page.getByRole("button", { name: "Concluir" }).click();
});

test("a waiter has no Caixa tab and is told there is no permission, without leaking why", async ({ page }) => {
  await entrarComo(page, "garcom");

  await expect(page.getByRole("link", { name: "Comandas" })).toBeVisible();
  await expect(page.getByRole("link", { name: "Caixa" })).toHaveCount(0);
  await page.goto("/salao/caixa");
  await expect(page.getByText("Você não tem permissão para ver o caixa.")).toBeVisible();
  await shoot(page, "10-caixa-sem-permissao");
});
