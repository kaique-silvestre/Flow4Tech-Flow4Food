import { mkdirSync } from "node:fs";
import { join } from "node:path";
import { type APIRequestContext, expect, type Locator, type Page, test } from "@playwright/test";
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
  return { id: comanda.id, auth };
}

/** Buttons and sheets animate; a screenshot must show the state at rest, in light and in dark. */
async function congelarTransicoes(page: Page) {
  await page.addStyleTag({
    content: "*, *::before, *::after { transition: none !important; animation: none !important; }",
  });
}

async function shootNosTemas(page: Page, name: string) {
  for (const tema of ["light", "dark"] as const) {
    await page.emulateMedia({ colorScheme: tema });
    await shoot(page, `${name}-${tema}`);
  }

  await page.emulateMedia({ colorScheme: "light" });
}

async function abrirCaixaSeFechado(page: Page) {
  await page.getByRole("link", { name: "Caixa" }).click();
  const fechado = page.getByText("Caixa fechado. Abra o caixa para receber.");
  await expect(fechado.or(page.getByRole("heading", { level: 2, name: "Caixa aberto" }))).toBeVisible();
  if (await fechado.isVisible()) {
    await page.getByLabel("Fundo de troco").fill("0");
    await page.getByRole("button", { name: "Abrir caixa" }).click();
  }

  await expect(page.getByRole("heading", { level: 2, name: "Caixa aberto" })).toBeVisible();
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

/** The numeric keypad of the sheet: one tap per digit, cents filling from the right. */
async function digitarNoTeclado(folha: Locator, digitos: string) {
  const teclado = folha.getByRole("group", { name: "Teclado numérico" });
  for (const d of digitos) {
    await teclado.getByRole("button", { name: d, exact: true }).click();
  }
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

test("the cashier receives a comanda in cash with change, partial first, then closes the shift and sees the difference in words", async ({
  page,
  request,
  baseURL,
}) => {
  const nome = `Conta ${Date.now() % 100000}`;
  await comandaDoGarcom(request, baseURL!, nome);
  await entrarComo(page, "caixa");

  // Cash register closed: the sheet says so and does not take the money.
  await fecharTurnoSeAberto(page);
  await abrirComanda(page, nome);
  await page.getByRole("button", { name: "Fechar conta" }).click();
  let folha = page.getByRole("dialog", { name: "Fechar conta" });
  await expect(folha.getByText("Abra o caixa para receber.")).toBeVisible();
  await folha.getByRole("button", { name: "Dinheiro", exact: true }).click();
  await expect(folha.getByRole("button", { name: "Receber", exact: true })).toBeDisabled();
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

  // The structure of the sheet: the active methods of the organization as chips (they come from the API), none chosen yet.
  await expect(folha.getByLabel("Valor a receber")).toContainText("27,50");
  const metodos = folha.getByRole("group", { name: /^Métodos/ });
  await expect(metodos.getByRole("button", { name: "Dinheiro", exact: true })).toBeVisible();
  await expect(metodos.getByRole("button", { name: "Pix" })).toBeVisible();
  await expect(folha.getByRole("button", { name: "Receber", exact: true })).toBeDisabled();
  await shoot(page, "14-fechar-conta");
  await semHorizontal(page);
  expect(await alvosPequenos(page)).toEqual([]);

  // The service fee removed (the cashier may): the total drops to R$ 25,00.
  await folha.getByRole("button", { name: "Remover serviço" }).click();
  await expect(folha.getByText("Serviço removido")).toBeVisible();
  await expect(folha.getByLabel("Valor a receber")).toContainText("25,00");
  await shoot(page, "14-servico-removido");

  // A partial payment of R$ 10,00 leaves R$ 15,00.
  await folha.getByRole("button", { name: "Dinheiro", exact: true }).click();
  await digitarNoTeclado(folha, "1000");
  await expect(folha.getByLabel("Resumo do recebimento")).toContainText("falta R$ 15,00");
  await shoot(page, "14-pagamento-parcial");
  await folha.getByRole("button", { name: "Receber", exact: true }).click();
  await expect(page.getByRole("dialog", { name: "Recebimento registrado" })).toBeVisible();
  await expect(page.getByText(/Restam/)).toContainText("15,00");
  await page.getByRole("button", { name: "Continuar" }).click();

  // The rest with R$ 20,00: R$ 5,00 of change in large type, then the comanda is closed.
  await page.getByRole("button", { name: "Fechar conta" }).click();
  folha = page.getByRole("dialog", { name: "Fechar conta" });
  await expect(folha.getByLabel("Valor a receber")).toContainText("15,00");
  await expect(folha.getByText("Já recebido")).toBeVisible();
  await folha.getByRole("button", { name: "Dinheiro", exact: true }).click();
  await digitarNoTeclado(folha, "2000");
  await expect(folha.locator(".fechar-troco-valor")).toContainText("5,00");
  await shoot(page, "14-troco");
  await folha.getByRole("button", { name: "Receber", exact: true }).click();
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

test("a payment taken with the signal cut waits in the queue and is registered once, closing the comanda, when the signal returns", async ({
  page,
  context,
  request,
  baseURL,
}) => {
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
  await folha.getByRole("button", { name: "Dinheiro", exact: true }).click();
  await folha.getByRole("button", { name: "Receber", exact: true }).click();
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

test("reopening the close sheet with a payment still in the queue is blocked: no second payment for the same amount", async ({
  page,
  context,
  request,
  baseURL,
}) => {
  const nome = `Dupla ${Date.now() % 100000}`;
  await comandaDoGarcom(request, baseURL!, nome);
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
  let folha = page.getByRole("dialog", { name: "Fechar conta" });
  await expect(folha.getByLabel("Valor a receber")).toContainText(/2[57],\d0/);

  await context.setOffline(true);
  await folha.getByRole("button", { name: "Dinheiro", exact: true }).click();
  await folha.getByRole("button", { name: "Receber", exact: true }).click();
  const naFila = page.getByRole("dialog", { name: "Recebimento na fila" });
  await expect(naFila).toBeVisible();
  await naFila.getByRole("button", { name: "Concluir" }).click();

  // Still offline: back into the same comanda, the sheet does not take the money again.
  await abrirComanda(page, nome);
  await page.getByRole("button", { name: "Fechar conta" }).click();
  folha = page.getByRole("dialog", { name: "Fechar conta" });
  await expect(folha.getByText(/esperando na fila/)).toBeVisible();
  await expect(folha.getByRole("button", { name: "Receber", exact: true })).toBeDisabled();
  await congelarTransicoes(page);
  await expect(folha).toHaveCSS("transform", /none|matrix\(1, 0, 0, 1, 0, 0\)/);
  await shootNosTemas(page, "14-recebimento-bloqueado-pela-fila");
  await folha.getByRole("button", { name: "Voltar" }).click();

  await context.setOffline(false);
  await expect(page.getByRole("button", { name: /ações? pendentes?/ })).toHaveCount(0, { timeout: 60_000 });
  // The one queued payment settled the bill and closed it: the comanda left the open list (a second payment under another id could not have).
  await page.getByRole("link", { name: "Comandas", exact: true }).first().click();
  await expect(page.getByRole("link", { name: new RegExp(nome) })).toHaveCount(0);
  await fecharTurnoSeAberto(page);
});

test("the queue bar shows the confirmation to remove a payment, the sending state and a stuck close", async ({
  page,
  context,
  request,
  baseURL,
}) => {
  // The stuck close needs five attempts with back-off (about 30 s).
  test.setTimeout(150_000);
  const nome = `Estados ${Date.now() % 100000}`;
  await comandaDoGarcom(request, baseURL!, nome);
  await entrarComo(page, "caixa");
  await abrirCaixaSeFechado(page);
  await abrirComanda(page, nome);
  await page.getByRole("button", { name: "Fechar conta" }).click();
  const folha = page.getByRole("dialog", { name: "Fechar conta" });
  await expect(folha.getByLabel("Valor a receber")).toContainText(/2[57],\d0/);
  await context.setOffline(true);
  await folha.getByRole("button", { name: "Dinheiro", exact: true }).click();
  await folha.getByRole("button", { name: "Receber", exact: true }).click();
  await page.getByRole("dialog", { name: "Recebimento na fila" }).getByRole("button", { name: "Concluir" }).click();
  await congelarTransicoes(page);

  // Confirmation before dropping money that was taken.
  await page.getByRole("button", { name: "1 ação pendente" }).click();
  await page.getByRole("button", { name: /^Remover Recebimento/ }).click();
  await expect(page.getByText("Remover este recebimento?")).toBeVisible();
  await semHorizontal(page);
  await shootNosTemas(page, "14-fila-confirmar-remocao");
  await page.getByRole("button", { name: "Manter na fila" }).click();
  await expect(page.getByText("Remover este recebimento?")).toHaveCount(0);

  // Sending: the read of the comanda is held, so the action stays in "Enviando".
  let soltar: () => void = () => undefined;
  const segurada = new Promise<void>((resolve) => {
    soltar = resolve;
  });
  await page.route("**/comandas/*/conta", async (rota) => {
    await segurada;
    await rota.continue();
  });
  // The close always fails: the payment is recorded, the close is what gets stuck.
  await page.route("**/comandas/*/fechar", (rota) => rota.fulfill({ status: 500, body: "{}" }));
  await context.setOffline(false);
  await expect(page.getByText("Enviando", { exact: true })).toBeVisible({ timeout: 20_000 });
  await semHorizontal(page);
  await shootNosTemas(page, "14-fila-enviando");
  soltar();

  // Stuck: after a bounded number of attempts the action shows as not sent, with the way out in words.
  await expect(page.getByText(/Não enviada: O recebimento foi registrado, mas a conta não fechou/)).toBeVisible({
    timeout: 120_000,
  });
  await semHorizontal(page);
  await shootNosTemas(page, "14-fila-fechamento-travado");

  await page.unroute("**/comandas/*/fechar");
  await page.getByRole("button", { name: /^Remover Recebimento/ }).click();
  await fecharTurnoSeAberto(page);
});
