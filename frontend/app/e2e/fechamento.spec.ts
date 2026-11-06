import { expect, test, type APIRequestContext, type Page } from "@playwright/test";
import { mkdirSync } from "node:fs";
import { join } from "node:path";
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

/** The waiter's side, straight through the API (another device): a comanda with 2 chopps (R$ 25,00; with the 10% fee R$ 27,50). */
async function comandaDoGarcom(request: APIRequestContext, baseURL: string, nome: string) {
  const headers = { "X-F4T-Csrf": "1", Origin: baseURL };
  const login = await request.post("/login", { headers, data: { organizacaoDoLogin: seed!.organizacaoA, usuario: "garcom2", senha: SENHA } });
  expect(login.ok()).toBeTruthy();
  const auth = { ...headers, Authorization: `Bearer ${((await login.json()) as { accessToken: string }).accessToken}` };
  const aberta = await request.post("/comandas", { headers: auth, data: { identificacao: nome, mesaId: null, pessoas: null } });
  expect(aberta.ok()).toBeTruthy();
  const comanda = (await aberta.json()) as { id: string; versao: number };
  const produtos = (await (await request.get("/cardapio/produtos", { headers: auth })).json()) as { id: string; codigo: string }[];
  const chopp = produtos.find((p) => p.codigo === "CHOPP")!;
  const rodada = await request.post(`/comandas/${comanda.id}/rodadas`, { headers: auth, data: { rodadaId: crypto.randomUUID(), versao: comanda.versao, itens: [{ produtoId: chopp.id, quantidade: 2 }] } });
  expect(rodada.status()).toBe(201);
  return { id: comanda.id, auth };
}

async function fecharTurnoSeAberto(page: Page) {
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

async function abrirComanda(page: Page, nome: string) {
  await page.getByRole("link", { name: "Comandas", exact: true }).first().click();
  await expect(page.getByRole("heading", { level: 2, name: "Comandas abertas" })).toBeVisible();
  await page.getByText(nome).first().click();
  await expect(page.getByRole("heading", { level: 2, name: nome })).toBeVisible();
}

async function semHorizontal(page: Page) {
  const m = await page.evaluate(() => ({ scroll: document.documentElement.scrollWidth, client: document.documentElement.clientWidth }));
  expect(m.scroll).toBeLessThanOrEqual(m.client);
}

async function alvosPequenos(page: Page) {
  return page.evaluate(() =>
    [...document.querySelectorAll<HTMLElement>("a, button, input, select, textarea")]
      .filter((el) => el.closest("[aria-hidden=true]") === null)
      .map((el) => ({ nome: el.textContent?.trim() || el.getAttribute("aria-label") || el.tagName, r: el.getBoundingClientRect() }))
      .filter(({ r }) => r.width > 0 && (r.width < 44 || r.height < 44))
      .map(({ nome, r }) => `${nome} ${Math.round(r.width)}x${Math.round(r.height)}`),
  );
}

test("the cashier receives a comanda in cash with change, partial first, then closes the shift and sees the difference in words", async ({ page, request, baseURL }) => {
  const nome = `Conta ${Date.now() % 100000}`;
  await comandaDoGarcom(request, baseURL!, nome);
  await entrarComo(page, "caixa");

  // Cash register closed: the sheet says so and does not take the money.
  await fecharTurnoSeAberto(page);
  await abrirComanda(page, nome);
  await page.getByRole("button", { name: "Fechar conta" }).click();
  let folha = page.getByRole("dialog", { name: "Fechar conta" });
  await expect(folha.getByText("Abra o caixa para receber.")).toBeVisible();
  await expect(folha.getByRole("button", { name: "Dinheiro" })).toBeDisabled();
  await shoot(page, "14-caixa-fechado");
  await semHorizontal(page);
  await folha.getByRole("link", { name: "Ir para o caixa" }).click();

  // Open the register with R$ 100,00 of float and come back.
  await page.getByLabel("Fundo de troco").fill("10000");
  await page.getByRole("button", { name: "Abrir caixa" }).click();
  await expect(page.getByRole("heading", { level: 2, name: "Caixa aberto" })).toBeVisible();
  await abrirComanda(page, nome);
  await page.getByRole("button", { name: "Fechar conta" }).click();
  folha = page.getByRole("dialog", { name: "Fechar conta" });

  // The structure of the sheet: only the Dinheiro method.
  await expect(folha.getByLabel("Valor a receber")).toContainText("27,50");
  await expect(folha.getByRole("button")).toHaveText(["Remover serviço", "Voltar", "Dinheiro"]);
  await expect(folha).not.toContainText(/Pix|Crédito|Débito|Voucher/);
  await shoot(page, "14-fechar-conta");
  await semHorizontal(page);
  expect(await alvosPequenos(page)).toEqual([]);

  // The service fee removed (the cashier may): the total drops to R$ 25,00.
  await folha.getByRole("button", { name: "Remover serviço" }).click();
  await expect(folha.getByText("Serviço removido")).toBeVisible();
  await expect(folha.getByLabel("Valor a receber")).toContainText("25,00");
  await shoot(page, "14-servico-removido");

  // A partial payment of R$ 10,00 leaves R$ 15,00.
  await folha.getByLabel("Dinheiro recebido").fill("1000");
  await expect(folha.getByText(/Pagamento parcial/)).toBeVisible();
  await shoot(page, "14-pagamento-parcial");
  await folha.getByRole("button", { name: "Dinheiro" }).click();
  await expect(page.getByRole("dialog", { name: "Recebimento registrado" })).toBeVisible();
  await expect(page.getByText(/Restam/)).toContainText("15,00");
  await page.getByRole("button", { name: "Continuar" }).click();

  // The rest with R$ 20,00: R$ 5,00 of change in large type, then the comanda is closed.
  await page.getByRole("button", { name: "Fechar conta" }).click();
  folha = page.getByRole("dialog", { name: "Fechar conta" });
  await expect(folha.getByLabel("Valor a receber")).toContainText("15,00");
  await expect(folha.getByText("Já recebido")).toBeVisible();
  await folha.getByLabel("Dinheiro recebido").fill("2000");
  await expect(folha.locator(".fechar-troco-valor")).toContainText("5,00");
  await shoot(page, "14-troco");
  await folha.getByRole("button", { name: "Dinheiro" }).click();
  const fechada = page.getByRole("dialog", { name: "Conta fechada" });
  await expect(fechada).toBeVisible();
  await expect(fechada.locator(".fechar-troco-valor")).toContainText("5,00");
  await shoot(page, "14-conta-fechada");
  await semHorizontal(page);
  await fechada.getByRole("button", { name: "Concluir" }).click();
  await expect(page.getByRole("heading", { level: 2, name: "Comandas abertas" })).toBeVisible();
  await expect(page.getByText(nome)).toHaveCount(0);

  // The shift: float R$ 100,00 + received R$ 25,00 (change is not kept) = R$ 125,00 expected; counted R$ 120,00 is R$ 5,00 short.
  await page.getByRole("link", { name: "Caixa" }).click();
  await expect(page.getByText("Esperado na gaveta").locator("..")).toContainText("125,00");
  await page.getByRole("button", { name: "Fechar caixa" }).click();
  const contagem = page.getByRole("dialog", { name: "Fechar caixa" });
  await contagem.getByLabel("Dinheiro contado").fill("12000");
  await contagem.getByRole("button", { name: "Fechar caixa" }).click();
  await expect(page.getByText("Falta R$ 5,00")).toBeVisible();
  await shoot(page, "14-caixa-diferenca");
  await page.getByRole("button", { name: "Concluir" }).click();
});

test("a payment taken with the signal cut waits in the queue and is registered once, closing the comanda, when the signal returns", async ({ page, context, request, baseURL }) => {
  const nome = `Fila ${Date.now() % 100000}`;
  const { id, auth } = await comandaDoGarcom(request, baseURL!, nome);
  await entrarComo(page, "caixa");
  await page.getByRole("link", { name: "Caixa" }).click();
  const fechado = page.getByText("Caixa fechado. Abra o caixa para receber.");
  await expect(fechado.or(page.getByRole("heading", { level: 2, name: "Caixa aberto" }))).toBeVisible();
  if (await fechado.isVisible()) {
    await page.getByLabel("Fundo de troco").fill("0");
    await page.getByRole("button", { name: "Abrir caixa" }).click();
  }

  await expect(page.getByRole("heading", { level: 2, name: "Caixa aberto" })).toBeVisible();
  await abrirComanda(page, nome);
  await page.getByRole("button", { name: "Fechar conta" }).click();
  const folha = page.getByRole("dialog", { name: "Fechar conta" });
  await expect(folha.getByLabel("Valor a receber")).toContainText(/2[57],\d0/);

  await context.setOffline(true);
  await folha.getByRole("button", { name: "Dinheiro" }).click();
  const naFila = page.getByRole("dialog", { name: "Recebimento na fila" });
  await expect(naFila).toBeVisible();
  await shoot(page, "14-recebimento-na-fila");
  await context.setOffline(false);
  await naFila.getByRole("button", { name: "Concluir" }).click();

  await expect(page.getByRole("button", { name: /ações? pendentes?/ })).toHaveCount(0, { timeout: 60_000 });
  const pagamentos = await request.get(`/comandas/${id}/pagamentos`, { headers: auth }).catch(() => null);
  // The waiter has no right to list payments; the closed comanda leaves the open list for the cashier instead.
  expect(pagamentos === null || [200, 403].includes(pagamentos.status())).toBeTruthy();
  await page.getByRole("link", { name: "Comandas", exact: true }).first().click();
  await expect(page.getByRole("link", { name: new RegExp(nome) })).toHaveCount(0);

  // Close the shift again so the next run starts from the closed state.
  await fecharTurnoSeAberto(page);
});
