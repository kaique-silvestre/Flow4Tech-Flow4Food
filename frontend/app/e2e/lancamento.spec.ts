import { appendFileSync, mkdirSync } from "node:fs";
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

/** A fresh comanda with a free name (tables are not needed here): the screen of the comanda is ready to launch. */
async function novaComanda(page: Page, nome: string) {
  await page.getByRole("button", { name: "Nova comanda" }).click();
  await page.getByRole("dialog", { name: "Nova comanda" }).getByRole("button", { name: "Dar um nome" }).click();
  await page.getByLabel("Nome da comanda").fill(nome);
  await page.getByRole("dialog", { name: "Dar um nome" }).getByRole("button", { name: "OK" }).click();
  await expect(page.getByRole("heading", { level: 2, name: nome })).toBeVisible();
}

const mais = (page: Page, produto: string) => page.getByRole("button", { name: `Adicionar ${produto}` });

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

test("from the signed-in list to the first item launched in under ten seconds, each next unit in one tap", async ({
  page,
}) => {
  await entrarComoGarcom2(page);
  const nome = `Tempo ${Date.now() % 100000}`;

  const inicio = Date.now();
  await novaComanda(page, nome);
  await mais(page, "Chopp 300ml").click();
  await page.getByRole("button", { name: "Enviar", exact: true }).click();
  await page.getByRole("dialog", { name: "Enviar rodada" }).getByRole("button", { name: "Enviar agora" }).click();
  await expect(page.getByText("Rodada enviada", { exact: false })).toBeVisible();
  const gasto = Date.now() - inicio;
  expect(gasto).toBeLessThan(10_000);
  // The measured target of the story (spec 21): scripted time from the signed-in list to the first item sent, and taps for the next unit (one).
  const tela = page.viewportSize();
  appendFileSync(
    join(shots, "..", "metas.txt"),
    `${new Date().toISOString()} ${tela?.width}x${tela?.height} primeiro item enviado em ${gasto} ms (limite 10000); proxima unidade do mesmo produto: 1 toque\n`,
  );

  // The next unit of the same product is a single tap.
  await mais(page, "Chopp 300ml").click();
  await expect(page.getByRole("region", { name: "Rodada não enviada" })).toContainText("1 item não enviado");
  await mais(page, "Chopp 300ml").click();
  await expect(page.getByRole("region", { name: "Rodada não enviada" })).toContainText("2 itens não enviados");
});

test("opens a comanda, adds items, sends, undoes within the window, sends again and cancels an item", async ({
  page,
}) => {
  await entrarComoGarcom2(page);
  const nome = `Fluxo ${Date.now() % 100000}`;
  await novaComanda(page, nome);
  await expect(page.getByText("Nenhum item ainda. Escolha abaixo.")).toBeVisible();
  await shoot(page, "12-comanda-vazia");
  await semHorizontal(page);
  expect(await alvosPequenos(page)).toEqual([]);

  // Stage: two chopps and a portion; the round bar says what is not sent.
  await mais(page, "Chopp 300ml").click();
  await mais(page, "Chopp 300ml").click();
  await page.getByRole("tab", { name: "Porções" }).click();
  await mais(page, "Porção de batata").click();
  const barra = page.getByRole("region", { name: "Rodada não enviada" });
  await expect(barra).toContainText("3 itens não enviados");
  await expect(barra).toContainText("Cozinha e bar só recebem ao enviar");
  await shoot(page, "12-rodada-em-preparo");
  await semHorizontal(page);
  expect(await alvosPequenos(page)).toEqual([]);

  // Dark theme with a staged round (docs/design/refs/salao/07.png).
  await page.emulateMedia({ colorScheme: "dark" });
  await shoot(page, "12-rodada-em-preparo-escuro");
  await page.emulateMedia({ colorScheme: "light" });

  // Send: the sheet, then the toast with the undo and its seconds.
  await page.getByRole("button", { name: "Enviar", exact: true }).click();
  const folha = page.getByRole("dialog", { name: "Enviar rodada" });
  await expect(folha).toContainText("Chopp 300ml");
  await expect(folha).toContainText("Porção de batata");
  await shoot(page, "12-enviar-rodada");
  await folha.getByRole("button", { name: "Enviar agora" }).click();
  const toast = page.getByRole("status").filter({ hasText: "Rodada enviada" });
  await expect(toast).toBeVisible();
  await shoot(page, "12-rodada-enviada-desfazer");
  await expect(page.getByRole("list", { name: "Itens da conta" })).toContainText("Chopp 300ml");
  await expect(page.getByLabel("Total da conta")).not.toContainText("R$ 0,00");

  // Undo inside the window: the items leave the bill.
  await toast.getByRole("button", { name: "Desfazer" }).click();
  await expect(page.getByText("Rodada desfeita. Os itens saíram da conta.")).toBeVisible();
  await expect(page.getByText("Nenhum item ainda. Escolha abaixo.")).toBeVisible();
  await expect(page.getByLabel("Total da conta")).toContainText("R$ 0,00");

  // Send again; after the window the undo is gone.
  await page.getByRole("tab", { name: "Bebidas" }).click();
  await mais(page, "Refrigerante lata").click();
  await mais(page, "Refrigerante lata").click();
  await page.getByRole("button", { name: "Enviar", exact: true }).click();
  await page.getByRole("dialog", { name: "Enviar rodada" }).getByRole("button", { name: "Enviar agora" }).click();
  await expect(page.getByRole("status").filter({ hasText: "Rodada enviada" })).toBeVisible();
  await expect(page.getByRole("button", { name: "Desfazer" })).toHaveCount(0, { timeout: 12_000 });

  // Cancel an item with a reason from the list: it leaves the bill.
  await page.getByRole("button", { name: "Cancelar Refrigerante lata" }).click();
  const cancelar = page.getByRole("dialog", { name: "Cancelar item" });
  await expect(cancelar.getByRole("button", { name: "Cancelar item" })).toBeDisabled();
  await cancelar.getByRole("button", { name: "Outro" }).click();
  await cancelar.getByLabel("Qual o motivo?").fill("cliente trocou de mesa");
  await shoot(page, "12-cancelar-item");
  await cancelar.getByRole("button", { name: "Cancelar item" }).click();
  await expect(page.getByRole("list", { name: "Itens da conta" })).not.toContainText("Refrigerante lata");
  await expect(page.getByLabel("Total da conta")).toContainText("R$ 0,00");
  await shoot(page, "12-item-cancelado");
});

test("repeats the last round and finds products by code, with a clear message when nothing matches", async ({
  page,
}) => {
  await entrarComoGarcom2(page);
  await novaComanda(page, `Repetir ${Date.now() % 100000}`);
  await page.getByRole("tab", { name: "Bebidas" }).click();
  await mais(page, "Água sem gás").click();
  await page.getByRole("button", { name: "Enviar", exact: true }).click();
  await page.getByRole("dialog", { name: "Enviar rodada" }).getByRole("button", { name: "Enviar agora" }).click();
  await expect(page.getByRole("status").filter({ hasText: "Rodada enviada" })).toBeVisible();

  await page.getByRole("button", { name: "Repetir rodada" }).click();

  await expect(page.getByRole("region", { name: "Rodada não enviada" })).toContainText("1 item não enviado");
  await page.getByLabel("Buscar produto").fill("batata");
  await expect(mais(page, "Porção de batata")).toBeVisible();
  await page.getByLabel("Buscar produto").fill("xyz");
  await expect(page.getByText("Nada com “xyz”. Tente o código do produto.")).toBeVisible();
  await shoot(page, "12-busca-sem-resultado");
});
