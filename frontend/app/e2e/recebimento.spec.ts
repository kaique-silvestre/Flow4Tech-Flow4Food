import { mkdirSync } from "node:fs";
import { join } from "node:path";
import { type APIRequestContext, expect, type Locator, type Page, test } from "@playwright/test";
import { lerSeed, SENHA } from "./seed";

// F4-0064: receive with the methods of the organization (chips from the API), several lines, change, tip, partial, a part of a split and the sad states.
// Written against the real API and the seed (docs/agents/project.md); the screenshots are the design review's evidence, light and dark.
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
  await page.screenshot({ path: join(shots, `${name}-${size?.width ?? 0}x${size?.height ?? 0}.png`) });
}

async function shootNosTemas(page: Page, name: string) {
  for (const tema of ["light", "dark"] as const) {
    await page.emulateMedia({ colorScheme: tema });
    await shoot(page, `${name}-${tema}`);
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

/** The waiter's side through the API (another device): a comanda with 2 chopps, R$ 25,00 (R$ 27,50 with the 10% fee). */
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

async function abrirFolha(page: Page, nome: string) {
  await abrirComanda(page, nome);
  await page.getByRole("button", { name: "Fechar conta" }).click();
  const folha = page.getByRole("dialog", { name: "Fechar conta" });
  await expect(folha.getByLabel("Valor a receber")).toContainText(/2[57],\d0|13,75/);
  return folha;
}

const chip = (folha: Locator, nome: string) =>
  folha.getByRole("group", { name: /^Métodos/ }).getByRole("button", { name: nome, exact: true });

async function digitar(folha: Locator, digitos: string, tocar: (alvo: Locator) => Promise<void>) {
  const teclado = folha.getByRole("group", { name: "Teclado numérico" });
  for (const d of digitos) {
    await tocar(teclado.getByRole("button", { name: d, exact: true }));
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

const sufixo = () => `${Date.now() % 100000}`;

test("Pix and cash on one bill in at most 8 taps, the balance live, then the comanda closes", async ({
  page,
  request,
  baseURL,
}) => {
  const nome = `Misto ${sufixo()}`;
  await comandaDoGarcom(request, baseURL!, nome);
  await entrarComo(page, "caixa");
  await abrirCaixaSeFechado(page);
  await abrirComanda(page, nome);

  let toques = 0;
  const tocar = async (alvo: Locator) => {
    toques += 1;
    await alvo.click();
  };
  await tocar(page.getByRole("button", { name: "Fechar conta" }));
  const folha = page.getByRole("dialog", { name: "Fechar conta" });
  await expect(folha.getByLabel("Valor a receber")).toContainText(/2[57],\d0/);

  // The chips are the active methods the API sent (none hard-coded in the app): at least cash and Pix, the factory ones.
  await expect(chip(folha, "Pix")).toBeVisible();
  await expect(chip(folha, "Dinheiro")).toBeVisible();
  await tocar(chip(folha, "Pix"));
  await tocar(chip(folha, "Dinheiro"));
  // R$ 10,00 in cash, typed on the keypad (4 taps): Pix takes what is left.
  await digitar(folha, "1000", tocar);
  const resumo = folha.getByLabel("Resumo do recebimento");
  await expect(resumo).toContainText("falta R$ 0,00");
  await expect(folha.getByRole("button", { name: /^Pix: / })).toContainText(/1[57],\d0/);
  await expect(folha.getByRole("button", { name: /^Dinheiro: / })).toContainText("10,00");
  await shootNosTemas(page, "15-misto");
  await semHorizontal(page);
  expect(await alvosPequenos(page)).toEqual([]);
  await tocar(folha.getByRole("button", { name: "Receber", exact: true }));
  await expect(page.getByRole("dialog", { name: "Conta fechada" })).toBeVisible();
  console.log(`receber misto: ${toques} toques (Fechar conta, 2 métodos, 4 dígitos, Receber)`);
  expect(toques).toBeLessThanOrEqual(8);
  await shootNosTemas(page, "15-misto-fechada");
  await page.getByRole("button", { name: "Concluir" }).click();
  await fecharTurnoSeAberto(page);
});

test("cash over the balance shows the change in large type, and the tip stays outside the balance", async ({
  page,
  request,
  baseURL,
}) => {
  const nome = `Troco ${sufixo()}`;
  await comandaDoGarcom(request, baseURL!, nome);
  await entrarComo(page, "caixa");
  await abrirCaixaSeFechado(page);
  const folha = await abrirFolha(page, nome);

  await chip(folha, "Dinheiro").click();
  await digitar(folha, "5000", (alvo) => alvo.click());
  await expect(folha.locator(".fechar-troco-valor")).toContainText(/2[23],\d0/);
  await folha.getByRole("button", { name: /^Gorjeta/ }).click();
  await digitar(folha, "300", (alvo) => alvo.click());
  await expect(folha.getByLabel("Resumo do recebimento")).toContainText("gorjeta R$ 3,00");
  await shootNosTemas(page, "15-troco-gorjeta");
  await semHorizontal(page);
  await folha.getByRole("button", { name: "Receber", exact: true }).click();
  const fechada = page.getByRole("dialog", { name: "Conta fechada" });
  await expect(fechada.locator(".fechar-troco-valor")).toContainText(/2[23],\d0/);
  await fechada.getByRole("button", { name: "Concluir" }).click();
  await fecharTurnoSeAberto(page);
});

test("a partial payment leaves the comanda open with what remains, and the rest closes it", async ({
  page,
  request,
  baseURL,
}) => {
  const nome = `Parcial ${sufixo()}`;
  await comandaDoGarcom(request, baseURL!, nome);
  await entrarComo(page, "caixa");
  await abrirCaixaSeFechado(page);
  let folha = await abrirFolha(page, nome);

  await chip(folha, "Pix").click();
  await digitar(folha, "1000", (alvo) => alvo.click());
  await expect(folha.getByLabel("Resumo do recebimento")).toContainText(/falta R\$ 1[57],\d0/);
  await folha.getByRole("button", { name: "Receber", exact: true }).click();
  const registrado = page.getByRole("dialog", { name: "Recebimento registrado" });
  await expect(registrado.getByText("A comanda segue aberta.")).toBeVisible();
  await expect(registrado.getByText(/Restam/)).toContainText(/1[57],\d0/);
  await shootNosTemas(page, "15-parcial-registrada");
  await registrado.getByRole("button", { name: "Continuar" }).click();

  await page.getByRole("button", { name: "Fechar conta" }).click();
  folha = page.getByRole("dialog", { name: "Fechar conta" });
  await expect(folha.getByText("Já recebido")).toBeVisible();
  await chip(folha, "Pix").click();
  await folha.getByRole("button", { name: "Receber", exact: true }).click();
  await expect(page.getByRole("dialog", { name: "Conta fechada" })).toBeVisible();
  await page.getByRole("button", { name: "Concluir" }).click();
  await fecharTurnoSeAberto(page);
});

test("with a split, the sheet says whose part is being paid and the comanda stays open until the last part", async ({
  page,
  request,
  baseURL,
}) => {
  const nome = `Parte ${sufixo()}`;
  await comandaDoGarcom(request, baseURL!, nome);
  await entrarComo(page, "caixa");
  await abrirCaixaSeFechado(page);
  await abrirComanda(page, nome);

  // Two equal parts: R$ 13,75 each (with the fee).
  await page.getByRole("button", { name: "Mais" }).click();
  await page.getByRole("button", { name: "Dividir a conta", exact: true }).click();
  await page.getByRole("button", { name: "Confirmar divisão" }).click();
  const dividida = page.getByRole("dialog", { name: "Conta dividida" });
  await expect(dividida).toBeVisible();
  await dividida.getByRole("button", { name: "Fechar", exact: true }).click();

  await page.getByRole("button", { name: "Fechar conta" }).click();
  let folha = page.getByRole("dialog", { name: "Fechar conta" });
  await expect(folha.getByLabel("Valor a receber")).toContainText("Parte 1");
  await expect(folha.getByLabel("Valor a receber")).toContainText("13,75");
  await shootNosTemas(page, "15-parte-da-divisao");
  await semHorizontal(page);
  await chip(folha, "Pix").click();
  await folha.getByRole("button", { name: "Receber", exact: true }).click();
  const registrado = page.getByRole("dialog", { name: "Recebimento registrado" });
  await expect(registrado.getByText(/Parte 1 recebida/)).toBeVisible();
  await expect(registrado.getByText(/Restam/)).toContainText("13,75");
  await registrado.getByRole("button", { name: "Continuar" }).click();

  // The next part is the one offered now.
  await page.getByRole("button", { name: "Fechar conta" }).click();
  folha = page.getByRole("dialog", { name: "Fechar conta" });
  await expect(folha.getByLabel("Valor a receber")).toContainText("Parte 2");
  await chip(folha, "Dinheiro").click();
  await folha.getByRole("button", { name: "Receber", exact: true }).click();
  await expect(page.getByRole("dialog", { name: "Conta fechada" })).toBeVisible();
  await page.getByRole("button", { name: "Concluir" }).click();
  await fecharTurnoSeAberto(page);
});

test("states: error from the server keeps the lines, no signal blocks other methods in words, cash alone still queues", async ({
  page,
  context,
  request,
  baseURL,
}) => {
  const nome = `Estados ${sufixo()}`;
  await comandaDoGarcom(request, baseURL!, nome);
  await entrarComo(page, "caixa");
  await abrirCaixaSeFechado(page);
  const folha = await abrirFolha(page, nome);

  // Error: the server answers 500; the sheet says so, keeps the lines and the same ids go again (one payment, never two).
  await chip(folha, "Pix").click();
  await page.route("**/comandas/*/recebimentos", (rota) => rota.fulfill({ status: 500, body: "{}" }));
  await folha.getByRole("button", { name: "Receber", exact: true }).click();
  await expect(folha.getByRole("alert")).toContainText("Não foi possível registrar o recebimento");
  await expect(folha.getByRole("button", { name: /^Pix: / })).toBeVisible();
  await shootNosTemas(page, "15-erro");
  await page.unroute("**/comandas/*/recebimentos");

  // Offline: Pix needs the server; the reason is in the footer and the button is off, nothing is queued.
  await context.setOffline(true);
  await expect(folha.getByText(/Sem conexão\. Receber com outro método/)).toBeVisible();
  await expect(folha.getByRole("button", { name: "Receber", exact: true })).toBeDisabled();
  await shootNosTemas(page, "15-sem-sinal-pix");
  await semHorizontal(page);

  // Offline with cash alone: the old route, the payment waits in the visible queue.
  await chip(folha, "Pix").click();
  await chip(folha, "Dinheiro").click();
  await folha.getByRole("button", { name: "Receber", exact: true }).click();
  const naFila = page.getByRole("dialog", { name: "Recebimento na fila" });
  await expect(naFila).toBeVisible();
  await shootNosTemas(page, "15-na-fila");
  await context.setOffline(false);
  await naFila.getByRole("button", { name: "Concluir" }).click();
  await expect(page.getByRole("button", { name: /ações? pendentes?/ })).toHaveCount(0, { timeout: 60_000 });
  await fecharTurnoSeAberto(page);
});
