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
  await page.getByLabel("Código da organização").fill(seed?.organizacaoA ?? "");
  await page.getByLabel("Senha").fill(SENHA);
  await page.getByRole("button", { name: "Entrar" }).click();
  await expect(page.getByRole("heading", { level: 1, name: "Salão" })).toBeVisible();
}

async function semHorizontal(page: Page) {
  const m = await page.evaluate(() => ({
    scroll: document.documentElement.scrollWidth,
    client: document.documentElement.clientWidth,
  }));
  expect(m.scroll).toBeLessThanOrEqual(m.client);
}

/** Every visible control is at least 44x44. */
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

/** Leaves the shift of the signed-in cashier closed (a run that stopped halfway may have left it open). */
async function comecarFechado(page: Page) {
  await page.getByRole("link", { name: "Caixa" }).click();
  const aberto = page.getByRole("heading", { level: 2, name: "Caixa aberto" });
  const fechado = page.getByText("Caixa fechado. Abra o caixa para receber.");
  await expect(aberto.or(fechado)).toBeVisible();
  if (await aberto.isVisible()) {
    await page.getByRole("button", { name: "Fechar caixa" }).click();
    const folha = page.getByRole("dialog", { name: "Fechar caixa" });
    await folha.getByLabel("Dinheiro contado").fill("0");
    await folha.getByRole("button", { name: "Fechar caixa" }).click();
    await page.getByRole("button", { name: "Concluir" }).click();
  }

  await expect(fechado).toBeVisible();
}

async function abrirCom(page: Page, fundo: string) {
  await page.getByLabel("Fundo de troco").fill(fundo);
  await page.getByRole("button", { name: "Abrir caixa" }).click();
  await expect(page.getByRole("heading", { level: 2, name: "Caixa aberto" })).toBeVisible();
}

test("the cashier supplies and withdraws, sees the movements of the shift and the expected cash follows", async ({
  page,
}) => {
  await entrarComo(page, "caixa");
  await comecarFechado(page);
  await abrirCom(page, "10000");
  await expect(page.getByText("Nenhum suprimento ou sangria neste turno.")).toBeVisible();
  await shoot(page, "23-caixa-aberto-sem-movimentos");

  // Supply R$ 50,00: the expected cash goes to R$ 150,00.
  await page.getByRole("button", { name: "Suprimento", exact: true }).click();
  const suprimento = page.getByRole("dialog", { name: "Suprimento" });
  await expect(suprimento.getByRole("button", { name: "Registrar suprimento" })).toBeDisabled();
  await suprimento.getByLabel("Valor").fill("5000");
  await suprimento.getByLabel("Motivo").fill("Mais troco");
  await shoot(page, "23-suprimento");
  await suprimento.getByRole("button", { name: "Registrar suprimento" }).click();
  await expect(page.getByText(/Suprimento de R\$\s50,00 registrado/)).toBeVisible();
  await expect(page.getByText("Esperado na gaveta").locator("..")).toContainText("150,00");

  // A withdrawal needs a destination; one bigger than the drawer is refused and keeps what was typed.
  await page.getByRole("button", { name: "Sangria", exact: true }).click();
  const sangria = page.getByRole("dialog", { name: "Sangria" });
  await sangria.getByLabel("Valor").fill("99999900");
  await sangria.getByLabel("Motivo").fill("Ao cofre");
  await expect(sangria.getByRole("button", { name: "Registrar sangria" })).toBeDisabled();
  await sangria.getByLabel("Destino do dinheiro").fill("Cofre");
  await sangria.getByRole("button", { name: "Registrar sangria" }).click();
  await expect(sangria.getByRole("alert")).toBeVisible();
  await shoot(page, "23-sangria-recusada");
  await sangria.getByLabel("Valor").fill("2000");
  await sangria.getByRole("button", { name: "Registrar sangria" }).click();
  await expect(page.getByText(/Sangria de R\$\s20,00 registrado/)).toBeVisible();
  await expect(page.getByText("Esperado na gaveta").locator("..")).toContainText("130,00");

  const movimentos = page.getByRole("region", { name: "Movimentos do turno" });
  await expect(movimentos).toContainText("Mais troco");
  await expect(movimentos).toContainText("Ao cofre · Cofre");
  await shoot(page, "23-movimentos");
  await semHorizontal(page);
  expect(await alvosPequenos(page)).toEqual([]);

  // Close with the exact count of cash: the difference is in words.
  await page.getByRole("button", { name: "Fechar caixa" }).click();
  const folha = page.getByRole("dialog", { name: "Fechar caixa" });
  await expect(folha.getByText(/Esperado na gaveta/)).toBeVisible();
  await folha.getByLabel("Dinheiro contado").fill("13000");
  await expect(folha.getByText("Confere", { exact: true })).toBeVisible();
  await shoot(page, "23-fechar-contagem");
  await folha.getByRole("button", { name: "Fechar caixa" }).click();
  await expect(page.getByRole("dialog", { name: "Caixa fechado" }).getByText("Conferido")).toBeVisible();
  await shoot(page, "23-fechado-confere");
  await page.getByRole("button", { name: "Concluir" }).click();
});

test("a difference above the tolerance closes as pending and a manager approves it with a reason", async ({
  page,
  browser,
  baseURL,
}) => {
  await entrarComo(page, "caixa");
  await comecarFechado(page);
  await abrirCom(page, "10000");
  await page.getByRole("button", { name: "Fechar caixa" }).click();
  const folha = page.getByRole("dialog", { name: "Fechar caixa" });
  await folha.getByLabel("Dinheiro contado").fill("2000");
  await folha.getByRole("button", { name: "Fechar caixa" }).click();
  const resultado = page.getByRole("dialog", { name: "Caixa fechado" });
  await expect(resultado.getByText(/Aguardando aprovação/)).toBeVisible();
  await expect(resultado.getByText("Falta R$ 80,00")).toBeVisible();
  await shoot(page, "23-fechado-pendente");
  await page.getByRole("button", { name: "Concluir" }).click();

  // The cashier does not see the manager's list.
  await expect(page.getByRole("heading", { name: "Diferenças para aprovar" })).toHaveCount(0);

  // The owner (who did not close it) sees the list and decides.
  const viewport = page.viewportSize();
  const gerente = await browser.newPage({ ...(baseURL ? { baseURL } : {}), ...(viewport ? { viewport } : {}) });
  await entrarComo(gerente, "dono@bar-a.test");
  await gerente.getByRole("link", { name: "Caixa" }).click();
  await expect(gerente.getByRole("heading", { level: 3, name: "Diferenças para aprovar" })).toBeVisible();
  await shoot(gerente, "23-pendentes");
  await semHorizontal(gerente);
  await gerente
    .getByRole("button", { name: /Caixa de/ })
    .first()
    .click();
  const detalhe = gerente.getByRole("dialog", { name: "Diferença do caixa" });
  await expect(detalhe.getByText(/Aguardando aprovação/)).toBeVisible();
  await shoot(gerente, "23-pendente-detalhe");
  await detalhe.getByRole("button", { name: "Aprovar", exact: true }).click();
  const decisao = gerente.getByRole("dialog", { name: "Aprovar diferença" });
  await expect(decisao.getByRole("button", { name: "Aprovar", exact: true })).toBeDisabled();
  await decisao.getByLabel("Motivo").fill("Conferido com o caixa");
  await decisao.getByRole("button", { name: "Aprovar", exact: true }).click();
  await expect(gerente.getByText("Diferença aprovada.")).toBeVisible();
  await shoot(gerente, "23-pendente-aprovada");
  await gerente.close();
});

test("a waiter is told there is no permission and never sees the movements or the manager list", async ({ page }) => {
  await entrarComo(page, "garcom");
  await page.goto("/salao/caixa");

  await expect(page.getByText("Você não tem permissão para ver o caixa.")).toBeVisible();
  await expect(page.getByRole("heading", { name: "Diferenças para aprovar" })).toHaveCount(0);
});
