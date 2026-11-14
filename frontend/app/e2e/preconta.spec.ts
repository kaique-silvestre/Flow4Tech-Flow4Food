import { mkdirSync } from "node:fs";
import { join } from "node:path";
import { type APIRequestContext, expect, type Page, test } from "@playwright/test";
import { lerSeed, SENHA } from "./seed";

// F4-0068: pre-bill, "conta pedida" and the receipt of a closed comanda. Written against the real API; not run in the ticket (no captures were asked for).
const shots = join(import.meta.dirname, "..", "e2e-results", "screenshots");
mkdirSync(shots, { recursive: true });

const seed = lerSeed();
test.skip(!process.env["E2E_API"] || seed === null, "needs the API running, the seed and E2E_API=1");

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

async function entrarComo(page: Page, usuario: string) {
  await page.goto("/salao/entrar");
  await page.getByLabel("Usuário ou e-mail").fill(usuario);
  await page.getByLabel("Código da organização").fill(seed!.organizacaoA);
  await page.getByLabel("Senha").fill(SENHA);
  await page.getByRole("button", { name: "Entrar" }).click();
  await expect(page.getByRole("heading", { level: 1, name: "Salão" })).toBeVisible();
}

/** Another device: garcom2 opens a comanda with 2 chopps (R$ 25,00; with the 10% fee R$ 27,50). */
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
  return { id: comanda.id };
}

async function abrirComanda(page: Page, nome: string) {
  await page.getByRole("link", { name: "Comandas", exact: true }).first().click();
  await expect(page.getByRole("heading", { level: 2, name: "Comandas abertas" })).toBeVisible();
  await page.getByText(nome).first().click();
  await expect(page.getByRole("heading", { level: 2, name: nome })).toBeVisible();
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

test("the waiter shows the pre-bill with the server's numbers and marks the bill as requested; the receipt is not his", async ({
  page,
  request,
  baseURL,
}) => {
  const nome = `Pre ${Date.now() % 100000}`;
  await comandaDoGarcom(request, baseURL!, nome);
  await entrarComo(page, "garcom2");
  await abrirComanda(page, nome);

  await page.getByRole("button", { name: "Ver pré-conta" }).click();
  const folha = page.getByRole("dialog", { name: "Pré-conta" });
  await expect(folha.getByText("Sem valor fiscal")).toBeVisible();
  await expect(folha.getByText("Chopp")).toBeVisible();
  await expect(folha.getByLabel("Conta")).toContainText("27,50");
  await expect(folha.getByRole("button", { name: "Compartilhar" })).toBeEnabled();
  await shoot(page, "25-pre-conta");
  await semHorizontal(page);
  expect(await alvosPequenos(page)).toEqual([]);

  await folha.getByRole("button", { name: "Pedir a conta" }).click();
  await expect(folha.getByRole("button", { name: /^Conta pedida/ })).toBeDisabled();
  await shoot(page, "25-conta-pedida");
  await folha.getByRole("button", { name: "Voltar" }).click();
  await expect(page.getByText(/Aguardando conta/)).toBeVisible();
  await expect(page.getByRole("button", { name: "Ver recibo" })).toHaveCount(0);
});

test("the pre-bill says so when the connection drops, without losing the document", async ({
  page,
  request,
  baseURL,
  context,
}) => {
  const nome = `Off ${Date.now() % 100000}`;
  await comandaDoGarcom(request, baseURL!, nome);
  await entrarComo(page, "garcom2");
  await abrirComanda(page, nome);
  await page.getByRole("button", { name: "Ver pré-conta" }).click();
  const folha = page.getByRole("dialog", { name: "Pré-conta" });
  await expect(folha.getByLabel("Conta")).toContainText("27,50");

  await context.setOffline(true);
  await expect(folha.getByText(/Sem conexão\. Pedir a conta e compartilhar/)).toBeVisible();
  await expect(folha.getByRole("button", { name: "Pedir a conta" })).toBeDisabled();
  await expect(folha.getByLabel("Conta")).toContainText("27,50");
  await shoot(page, "25-pre-conta-offline");
  await context.setOffline(false);
});

test("the cashier closes a comanda and opens its receipt (second copy)", async ({ page, request, baseURL }) => {
  const nome = `Rec ${Date.now() % 100000}`;
  await comandaDoGarcom(request, baseURL!, nome);
  await entrarComo(page, "caixa");

  await page.getByRole("link", { name: "Caixa" }).click();
  const fechado = page.getByText("Caixa fechado. Abra o caixa para receber.");
  await expect(fechado.or(page.getByRole("heading", { level: 2, name: "Caixa aberto" }))).toBeVisible();
  if (await fechado.isVisible()) {
    await page.getByLabel("Fundo de troco").fill("10000");
    await page.getByRole("button", { name: "Abrir caixa" }).click();
  }

  await expect(page.getByRole("heading", { level: 2, name: "Caixa aberto" })).toBeVisible();
  await abrirComanda(page, nome);
  await page.getByRole("button", { name: "Fechar conta" }).click();
  const folha = page.getByRole("dialog", { name: "Fechar conta" });
  await folha
    .getByRole("group", { name: /^Métodos/ })
    .getByRole("button", { name: "Pix" })
    .click();
  await folha.getByRole("button", { name: "Receber", exact: true }).click();
  await expect(page.getByRole("dialog", { name: "Conta fechada" })).toBeVisible();

  await page.getByRole("button", { name: "Ver recibo" }).click();
  await expect(page.getByRole("heading", { level: 2, name: /Recibo/ })).toBeVisible();
  await expect(page.getByText("2ª via")).toBeVisible();
  await expect(page.getByText("Sem valor fiscal")).toBeVisible();
  await expect(page.getByLabel("Pagamentos")).toContainText("Pix");
  await expect(page.getByLabel("Conta")).toContainText("27,50");
  await shoot(page, "25-recibo");
  await semHorizontal(page);
  expect(await alvosPequenos(page)).toEqual([]);
});
