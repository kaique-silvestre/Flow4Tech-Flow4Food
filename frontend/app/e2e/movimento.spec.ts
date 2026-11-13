import { mkdirSync } from "node:fs";
import { join } from "node:path";
import AxeBuilder from "@axe-core/playwright";
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

async function shootNosTemas(page: Page, name: string) {
  for (const tema of ["light", "dark"] as const) {
    await page.emulateMedia({ colorScheme: tema });
    await shoot(page, `${name}-${tema}`);
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

async function semViolacoes(page: Page, tela: string) {
  for (const tema of ["light", "dark"] as const) {
    await page.emulateMedia({ colorScheme: tema });
    const r = await new AxeBuilder({ page }).withTags(["wcag2a", "wcag2aa", "wcag21a", "wcag21aa"]).analyze();
    expect(
      r.violations.map((v) => ({ regra: v.id, alvos: v.nodes.map((n) => n.target.join(" ")) })),
      `${tela}/${tema}`,
    ).toEqual([]);
  }

  await page.emulateMedia({ colorScheme: "light" });
}

/** Every control inside the sheet is at least 44x44. */
async function alvosPequenos(folha: Locator) {
  return folha.evaluate((raiz) =>
    [...raiz.querySelectorAll<HTMLElement>("a, button, input, select, textarea")]
      .map((el) => ({
        nome: el.textContent?.trim() || el.getAttribute("aria-label") || el.tagName,
        r: el.getBoundingClientRect(),
      }))
      .filter(({ r }) => r.width > 0 && (r.width < 44 || r.height < 44))
      .map(({ nome, r }) => `${nome} ${Math.round(r.width)}x${Math.round(r.height)}`),
  );
}

async function entrarComo(page: Page, quem: "dono" | "garcom2") {
  await page.goto("/salao/entrar");
  if (quem === "dono") {
    await page.getByLabel("Usuário ou e-mail").fill("dono@bar-a.test");
  } else {
    await page.getByLabel("Usuário ou e-mail").fill("garcom2");
    await page.getByLabel("Código da organização").fill(seed!.organizacaoA);
  }

  await page.getByLabel("Senha").fill(SENHA);
  await page.getByRole("button", { name: "Entrar" }).click();
  await expect(page.getByRole("heading", { level: 1, name: "Salão" })).toBeVisible();
}

interface ComandaDeTeste {
  id: string;
  versao: number;
  nome: string;
  auth: Record<string, string>;
}

async function acesso(request: APIRequestContext, baseURL: string, corpo: Record<string, unknown>) {
  const headers = { "X-F4T-Csrf": "1", Origin: baseURL };
  const login = await request.post("/login", { headers, data: corpo });
  expect(login.ok()).toBeTruthy();
  return { ...headers, Authorization: `Bearer ${((await login.json()) as { accessToken: string }).accessToken}` };
}

const comoGarcom = (request: APIRequestContext, baseURL: string) =>
  acesso(request, baseURL, { organizacaoDoLogin: seed!.organizacaoA, usuario: "garcom2", senha: SENHA });
const comoDono = (request: APIRequestContext, baseURL: string) =>
  acesso(request, baseURL, { email: "dono@bar-a.test", senha: SENHA });

/** A comanda made straight through the API (another device), with the named products launched in one round; independent of what the database already holds. */
async function comanda(
  request: APIRequestContext,
  baseURL: string,
  nome: string,
  itens: { codigo: string; quantidade: number }[] = [],
): Promise<ComandaDeTeste> {
  const auth = await comoGarcom(request, baseURL);
  const aberta = await request.post("/comandas", {
    headers: auth,
    data: { identificacao: nome, mesaId: null, pessoas: null },
  });
  expect(aberta.ok()).toBeTruthy();
  let versao = ((await aberta.json()) as { versao: number }).versao;
  const id = ((await aberta.json()) as { id: string }).id;
  if (itens.length > 0) {
    const produtos = (await (await request.get("/cardapio/produtos", { headers: auth })).json()) as {
      id: string;
      codigo: string;
    }[];
    const rodada = await request.post(`/comandas/${id}/rodadas`, {
      headers: auth,
      data: {
        rodadaId: crypto.randomUUID(),
        versao,
        itens: itens.map((i) => ({
          produtoId: produtos.find((p) => p.codigo === i.codigo)!.id,
          quantidade: i.quantidade,
        })),
      },
    });
    expect(rodada.status()).toBe(201);
    versao = ((await rodada.json()) as { comandaVersao: number }).comandaVersao;
  }

  return { id, versao, nome, auth };
}

async function abrirDaLista(page: Page, nome: string) {
  await page.getByRole("link", { name: new RegExp(nome) }).click();
  await expect(page.getByRole("heading", { level: 2, name: nome })).toBeVisible();
}

async function abrirMenu(page: Page, acao: string) {
  await page.getByRole("button", { name: "Mais" }).click();
  await page.getByRole("button", { name: acao, exact: true }).click();
}

const sufixo = () => `${Date.now() % 100000}`;

test("transfer: tick an item, pick the other comanda, the bill and the destination follow", async ({
  page,
  request,
  baseURL,
}) => {
  const s = sufixo();
  const origem = await comanda(request, baseURL!, `Origem ${s}`, [
    { codigo: "CHOPP", quantidade: 2 },
    { codigo: "REFRI", quantidade: 1 },
  ]);
  const destino = await comanda(request, baseURL!, `Destino ${s}`);
  await entrarComo(page, "dono");
  await abrirDaLista(page, origem.nome);
  await expect(page.getByLabel("Total da conta")).toContainText("35,20");

  await abrirMenu(page, "Transferir itens");
  const folha = page.getByRole("dialog", { name: "Transferir itens" });
  await expect(folha.getByRole("button", { name: "Transferir", exact: true })).toBeDisabled();
  await shootNosTemas(page, "transferir-vazio");
  await folha.getByRole("checkbox", { name: /Refrigerante lata/ }).check();
  await folha.getByRole("radio", { name: new RegExp(destino.nome) }).check();
  await expect(folha.getByText(/1 item · R\$\s*7,00 para/)).toBeVisible();
  await expect(folha.getByRole("button", { name: "Transferir", exact: true })).toBeInViewport();
  await semHorizontal(page);
  expect(await alvosPequenos(folha)).toEqual([]);
  await shootNosTemas(page, "transferir-escolhido");
  await semViolacoes(page, "transferir");

  await folha.getByRole("button", { name: "Transferir", exact: true }).click();
  await expect(folha).toBeHidden();
  await expect(page.getByText(`1 item transferido para ${destino.nome}.`)).toBeVisible();
  await expect(page.getByLabel("Total da conta")).toContainText("27,50");
  await shootNosTemas(page, "transferir-feito");

  const dono = await comoDono(request, baseURL!);
  const noDestino = (await (await request.get(`/comandas/${destino.id}/itens`, { headers: dono })).json()) as {
    itens: { produtoNome: string }[];
  };
  expect(noDestino.itens.map((i) => i.produtoNome)).toEqual(["Refrigerante lata"]);
});

test("transfer conflict: the destination changed on another device, the numbers refresh and the selection stays", async ({
  page,
  request,
  baseURL,
}) => {
  const s = sufixo();
  const origem = await comanda(request, baseURL!, `OrigemC ${s}`, [{ codigo: "REFRI", quantidade: 1 }]);
  const destino = await comanda(request, baseURL!, `DestinoC ${s}`);
  await entrarComo(page, "dono");
  await abrirDaLista(page, origem.nome);
  await abrirMenu(page, "Transferir itens");
  const folha = page.getByRole("dialog", { name: "Transferir itens" });
  await folha.getByRole("checkbox", { name: /Refrigerante lata/ }).check();
  await folha.getByRole("radio", { name: new RegExp(destino.nome) }).check();

  // Another device launches a round on the destination after this screen read its version.
  const produtos = (await (await request.get("/cardapio/produtos", { headers: destino.auth })).json()) as {
    id: string;
    codigo: string;
  }[];
  const outra = await request.post(`/comandas/${destino.id}/rodadas`, {
    headers: destino.auth,
    data: {
      rodadaId: crypto.randomUUID(),
      versao: destino.versao,
      itens: [{ produtoId: produtos.find((p) => p.codigo === "AGUA")!.id, quantidade: 1 }],
    },
  });
  expect(outra.status()).toBe(201);

  await folha.getByRole("button", { name: "Transferir", exact: true }).click();
  await expect(folha.getByRole("alert")).toContainText("outro aparelho");
  await expect(folha.getByRole("checkbox", { name: /Refrigerante lata/ })).toBeChecked();
  await expect(folha.getByRole("radio", { name: new RegExp(destino.nome) })).toBeChecked();
  await expect(folha.getByRole("button", { name: "Transferir", exact: true })).toBeInViewport();
  await shootNosTemas(page, "transferir-conflito");
  await folha.getByRole("button", { name: "Transferir", exact: true }).click();
  await expect(folha).toBeHidden();
  await expect(page.getByText(`1 item transferido para ${destino.nome}.`)).toBeVisible();
});

test("transfer refused: an item with a discount cannot leave, said in words, with the sheet and ticks kept", async ({
  page,
  request,
  baseURL,
}) => {
  const s = sufixo();
  const origem = await comanda(request, baseURL!, `OrigemD ${s}`, [{ codigo: "CHOPP", quantidade: 1 }]);
  const destino = await comanda(request, baseURL!, `DestinoD ${s}`);
  const dono = await comoDono(request, baseURL!);
  const itens = (await (await request.get(`/comandas/${origem.id}/itens`, { headers: dono })).json()) as {
    itens: { id: string }[];
  };
  const desconto = await request.post(`/comandas/${origem.id}/descontos`, {
    headers: dono,
    data: {
      versao: origem.versao,
      itemId: itens.itens[0]!.id,
      percentual: 10,
      valorCentavos: null,
      motivo: "Aniversário",
    },
  });
  expect(desconto.status()).toBe(201);

  await entrarComo(page, "dono");
  await abrirDaLista(page, origem.nome);
  await abrirMenu(page, "Transferir itens");
  const folha = page.getByRole("dialog", { name: "Transferir itens" });
  await folha.getByRole("checkbox", { name: /Chopp 300ml/ }).check();
  await folha.getByRole("radio", { name: new RegExp(destino.nome) }).check();
  await folha.getByRole("button", { name: "Transferir", exact: true }).click();
  await expect(folha.getByRole("alert")).toContainText("tem desconto");
  await expect(folha.getByRole("checkbox", { name: /Chopp 300ml/ })).toBeChecked();
  await expect(folha.getByRole("alert")).toBeInViewport();
  await shootNosTemas(page, "transferir-erro");
  await semViolacoes(page, "transferir-erro");
});

test("separate: ticked items leave for a new comanda with the given name", async ({ page, request, baseURL }) => {
  const s = sufixo();
  const origem = await comanda(request, baseURL!, `OrigemS ${s}`, [
    { codigo: "CHOPP", quantidade: 1 },
    { codigo: "AGUA", quantidade: 2 },
  ]);
  await entrarComo(page, "dono");
  await abrirDaLista(page, origem.nome);
  await abrirMenu(page, "Separar itens");
  const folha = page.getByRole("dialog", { name: "Separar itens" });
  await folha.getByRole("checkbox", { name: /Água sem gás/ }).check();
  await folha.getByLabel("Nome da comanda nova (opcional)").fill(`Primos ${s}`);
  await expect(folha.getByRole("button", { name: "Separar", exact: true })).toBeInViewport();
  await semHorizontal(page);
  expect(await alvosPequenos(folha)).toEqual([]);
  await shootNosTemas(page, "separar");
  await semViolacoes(page, "separar");
  await folha.getByRole("button", { name: "Separar", exact: true }).click();
  await expect(folha).toBeHidden();
  await expect(page.getByText(/1 item separado na comanda #\d+\./)).toBeVisible();
  await expect(page.getByLabel("Total da conta")).toContainText("13,75");
  const dono = await comoDono(request, baseURL!);
  const lista = (await (await request.get("/comandas", { headers: dono })).json()) as { identificacao: string }[];
  expect(lista.some((c) => c.identificacao === `Primos ${s}`)).toBe(true);
});

test("merge: pick the comanda, read the warning, confirm; the items arrive here. A waiter without the permission is not offered it", async ({
  page,
  request,
  baseURL,
}) => {
  const s = sufixo();
  const a = await comanda(request, baseURL!, `JuntaA ${s}`, [{ codigo: "CHOPP", quantidade: 1 }]);
  const b = await comanda(request, baseURL!, `JuntaB ${s}`, [{ codigo: "AGUA", quantidade: 1 }]);
  await entrarComo(page, "dono");
  await abrirDaLista(page, a.nome);
  await abrirMenu(page, "Juntar comandas");
  const folha = page.getByRole("dialog", { name: "Juntar comandas" });
  await expect(folha.getByRole("button", { name: "Continuar" })).toBeDisabled();
  await folha.getByRole("radio", { name: new RegExp(b.nome) }).check();
  expect(await alvosPequenos(folha)).toEqual([]);
  await shootNosTemas(page, "juntar-escolha");
  await folha.getByRole("button", { name: "Continuar" }).click();
  await expect(folha.getByText("Não dá para desfazer")).toBeVisible();
  await expect(folha.getByRole("button", { name: "Juntar comandas" })).toBeInViewport();
  await shootNosTemas(page, "juntar-confirmar");
  await semViolacoes(page, "juntar-confirmar");
  await folha.getByRole("button", { name: "Juntar comandas" }).click();
  await expect(folha).toBeHidden();
  await expect(page.getByText(new RegExp(`${b.nome} \\(#\\d+\\) foi juntada em ${a.nome}`))).toBeVisible();
  await expect(page.getByLabel("Total da conta")).toContainText("19,25");
  await expect(page.getByRole("list", { name: "Itens da conta" }).getByText("Água sem gás")).toBeVisible();
  await shootNosTemas(page, "juntar-feito");
});

test("negado: the waiter's menu has transfer, separate and split but not merge (no permission, no row)", async ({
  page,
  request,
  baseURL,
}) => {
  const s = sufixo();
  const a = await comanda(request, baseURL!, `Negado ${s}`, [{ codigo: "CHOPP", quantidade: 1 }]);
  await entrarComo(page, "garcom2");
  await abrirDaLista(page, a.nome);
  await page.getByRole("button", { name: "Mais" }).click();
  await expect(page.getByRole("button", { name: "Transferir itens" })).toBeVisible();
  await expect(page.getByRole("button", { name: "Separar itens" })).toBeVisible();
  await expect(page.getByRole("button", { name: "Dividir a conta" })).toBeVisible();
  await expect(page.getByRole("button", { name: "Juntar comandas" })).toHaveCount(0);
  await shootNosTemas(page, "menu-sem-juntar");
  await semViolacoes(page, "menu-sem-juntar");
});

test("offline: the sheets say they need a connection, ask nothing of the server and queue nothing", async ({
  page,
  request,
  baseURL,
  context,
}) => {
  const s = sufixo();
  const a = await comanda(request, baseURL!, `Offline ${s}`, [{ codigo: "CHOPP", quantidade: 1 }]);
  await comanda(request, baseURL!, `OffDestino ${s}`);
  await entrarComo(page, "dono");
  await abrirDaLista(page, a.nome);
  await context.setOffline(true);
  await abrirMenu(page, "Transferir itens");
  const folha = page.getByRole("dialog", { name: "Transferir itens" });
  await expect(
    folha.getByText(/Sem conexão\. Transferir itens precisa do servidor e não fica na fila\./),
  ).toBeVisible();
  await expect(folha.getByRole("button", { name: "Transferir", exact: true })).toBeDisabled();
  await shootNosTemas(page, "transferir-sem-conexao");
  await page.keyboard.press("Escape");
  await abrirMenu(page, "Dividir a conta");
  const dividir = page.getByRole("dialog", { name: "Dividir a conta" });
  await expect(
    dividir.getByText(/Sem conexão\. Dividir a conta precisa do servidor e não fica na fila\./),
  ).toBeVisible();
  await expect(dividir.getByRole("button", { name: "Confirmar divisão" })).toBeDisabled();
  await shootNosTemas(page, "dividir-sem-conexao");
  await context.setOffline(false);
  expect(await page.evaluate(() => localStorage.getItem("f4.fila.v1") ?? "[]")).not.toContain("divisao");
});

test("split equally in at most 4 taps, with the server's preview before confirming", async ({
  page,
  request,
  baseURL,
}) => {
  const s = sufixo();
  const a = await comanda(request, baseURL!, `Divide ${s}`, [
    { codigo: "CHOPP", quantidade: 2 },
    { codigo: "REFRI", quantidade: 1 },
  ]);
  await entrarComo(page, "garcom2");
  await abrirDaLista(page, a.nome);

  // From the comanda screen: Mais, Dividir a conta, "+" (three parts), Confirmar divisão.
  let toques = 0;
  const tocar = async (alvo: Locator) => {
    toques += 1;
    await alvo.click();
  };
  await tocar(page.getByRole("button", { name: "Mais" }));
  await tocar(page.getByRole("button", { name: "Dividir a conta", exact: true }));
  const folha = page.getByRole("dialog", { name: "Dividir a conta" });
  await expect(folha.getByRole("list", { name: "Partes da divisão" }).getByRole("listitem")).toHaveCount(2);
  await tocar(folha.getByRole("button", { name: "Aumentar Partes" }));
  const partes = folha.getByRole("list", { name: "Partes da divisão" }).getByRole("listitem");
  await expect(partes).toHaveCount(3);
  // R$ 32,00 with the 10% fee is R$ 35,20; three parts: 11,74 / 11,73 / 11,73 (the leftover cent goes to the first).
  await expect(partes.nth(0)).toContainText("11,74");
  await expect(partes.nth(2)).toContainText("11,73");
  await expect(folha.getByRole("button", { name: "Confirmar divisão" })).toBeInViewport();
  await semHorizontal(page);
  expect(await alvosPequenos(folha)).toEqual([]);
  await shootNosTemas(page, "dividir-iguais");
  await semViolacoes(page, "dividir-iguais");
  await tocar(folha.getByRole("button", { name: "Confirmar divisão" }));
  await expect(page.getByRole("dialog", { name: "Conta dividida" })).toBeVisible();
  console.log(`dividir em partes iguais: ${toques} toques (Mais, Dividir a conta, +, Confirmar divisão)`);
  expect(toques).toBeLessThanOrEqual(4);
  await shootNosTemas(page, "dividir-feita");
  await semViolacoes(page, "dividir-feita");
  await page.getByRole("button", { name: "Fechar", exact: true }).click();
  await expect(page.getByRole("dialog")).toHaveCount(0);

  const dono = await comoDono(request, baseURL!);
  const gravada = await request.get(`/comandas/${a.id}/divisao`, { headers: dono });
  expect(gravada.ok()).toBeTruthy();
  expect(((await gravada.json()) as { partes: unknown[] }).partes).toHaveLength(3);
});

test("split sheet: preview loading, preview failure with retry, and the four other ways", async ({
  page,
  request,
  baseURL,
}) => {
  const s = sufixo();
  const a = await comanda(request, baseURL!, `Modos ${s}`, [
    { codigo: "CHOPP", quantidade: 2 },
    { codigo: "REFRI", quantidade: 1 },
  ]);
  await entrarComo(page, "garcom2");
  await abrirDaLista(page, a.nome);

  // Loading: the preview is held back.
  await page.route("**/comandas/*/divisao/previa", async (rota) => {
    await new Promise((r) => setTimeout(r, 2500));
    await rota.continue();
  });
  await abrirMenu(page, "Dividir a conta");
  const folha = page.getByRole("dialog", { name: "Dividir a conta" });
  await expect(folha.getByText("Calculando a divisão…")).toBeVisible();
  await expect(folha.getByRole("button", { name: "Confirmar divisão" })).toBeDisabled();
  await shootNosTemas(page, "dividir-carregando");
  await expect(folha.getByRole("list", { name: "Partes da divisão" })).toBeVisible({ timeout: 10_000 });
  await page.unroute("**/comandas/*/divisao/previa");

  // Failure: the preview answers 500; the sheet says so and the retry works.
  await page.route("**/comandas/*/divisao/previa", (rota) => rota.fulfill({ status: 500, body: "" }));
  await folha.getByRole("button", { name: "Aumentar Partes" }).click();
  await expect(folha.getByRole("alert")).toContainText("Não foi possível");
  await expect(folha.getByRole("button", { name: "Confirmar divisão" })).toBeDisabled();
  await shootNosTemas(page, "dividir-erro");
  await page.unroute("**/comandas/*/divisao/previa");
  await folha.getByRole("button", { name: "Tentar novamente" }).click();
  await expect(folha.getByRole("list", { name: "Partes da divisão" }).getByRole("listitem")).toHaveCount(3);

  // By seat: nothing carries a seat, so the server refuses and the sheet explains in words.
  await folha.getByRole("button", { name: "Por assento" }).click();
  await expect(folha.getByRole("alert")).toContainText("assento");
  await expect(folha.getByRole("button", { name: "Dividir por item" })).toBeInViewport();
  await shootNosTemas(page, "dividir-assento");

  // By item.
  await folha.getByRole("button", { name: "Por item", exact: true }).click();
  await expect(folha.getByLabel("Parte de Refrigerante lata")).toBeVisible();
  // Three parts for two items leaves a part empty: said in words, nothing asked of the server. Two parts: the preview.
  await expect(folha.getByText(/pelo menos um item/)).toBeVisible();
  await folha.getByRole("button", { name: "Diminuir Partes" }).click();
  await expect(folha.getByRole("list", { name: "Partes da divisão" }).getByRole("listitem")).toHaveCount(2);
  await expect(folha.getByRole("button", { name: "Confirmar divisão" })).toBeInViewport();
  await semHorizontal(page);
  expect(await alvosPequenos(folha)).toEqual([]);
  await shootNosTemas(page, "dividir-por-item");
  await semViolacoes(page, "dividir-por-item");

  // Free values.
  await folha.getByRole("button", { name: "Valores livres" }).click();
  const primeiro = folha.getByLabel("Parte 1");
  await primeiro.fill("100");
  await expect(folha.getByText(/Faltam|Passou/)).toBeVisible();
  await expect(folha.getByRole("button", { name: "Confirmar divisão" })).toBeDisabled();
  await shootNosTemas(page, "dividir-valores-soma");
  await semViolacoes(page, "dividir-valores-soma");

  // Percent.
  await folha.getByRole("button", { name: "Percentual" }).click();
  await folha.getByRole("button", { name: "Aumentar Partes" }).click();
  await expect(folha.getByLabel("Parte 1 (%)")).toHaveValue("33,34");
  await expect(folha.getByRole("list", { name: "Partes da divisão" }).getByRole("listitem")).toHaveCount(3);
  await folha.getByLabel("Parte 1 (%)").fill("50");
  await expect(folha.getByText(/precisa ser 100%/)).toBeVisible();
  await folha.getByLabel("Parte 1 (%)").fill("34");
  await folha.getByLabel("Parte 2 (%)").fill("33");
  await folha.getByLabel("Parte 3 (%)").fill("33");
  await expect(folha.getByRole("button", { name: "Confirmar divisão" })).toBeEnabled();
  await semHorizontal(page);
  expect(await alvosPequenos(folha)).toEqual([]);
  await shootNosTemas(page, "dividir-percentual");
  await semViolacoes(page, "dividir-percentual");
});

test("the full menu (10 actions) fits or scrolls to the last one, grouped, in both themes", async ({
  page,
  request,
  baseURL,
}) => {
  const a = await comanda(request, baseURL!, `Menu ${sufixo()}`, [{ codigo: "CHOPP", quantidade: 1 }]);
  await entrarComo(page, "dono");
  await abrirDaLista(page, a.nome);
  await page.getByRole("button", { name: "Mais" }).click();
  const botoes = page.getByRole("list", { name: "Ações da comanda" }).getByRole("button");
  await expect(botoes).toHaveCount(10);
  await expect(page.getByRole("button", { name: "Juntar comandas" })).toBeVisible();
  await shootNosTemas(page, "menu-completo-topo");
  await botoes.last().scrollIntoViewIfNeeded();
  await expect(botoes.last()).toBeInViewport();
  await semHorizontal(page);
  await shootNosTemas(page, "menu-completo-fim");
  await semViolacoes(page, "menu-completo");
});

test("split preview and the difference stay in the pinned footer with five parts (percent and free values)", async ({
  page,
  request,
  baseURL,
}) => {
  const a = await comanda(request, baseURL!, `Cinco ${sufixo()}`, [
    { codigo: "CHOPP", quantidade: 2 },
    { codigo: "REFRI", quantidade: 1 },
  ]);
  await entrarComo(page, "garcom2");
  await abrirDaLista(page, a.nome);
  await abrirMenu(page, "Dividir a conta");
  const folha = page.getByRole("dialog", { name: "Dividir a conta" });
  await folha.getByRole("button", { name: "Percentual" }).click();
  for (let i = 0; i < 3; i += 1) {
    await folha.getByRole("button", { name: "Aumentar Partes" }).click();
  }

  const partes = folha.getByRole("list", { name: "Partes da divisão" }).getByRole("listitem");
  await expect(partes).toHaveCount(5);
  for (let i = 0; i < 5; i += 1) {
    await expect(partes.nth(i)).toBeInViewport();
  }

  await expect(folha.getByRole("button", { name: "Confirmar divisão" })).toBeInViewport();
  await semHorizontal(page);
  await shootNosTemas(page, "dividir-percentual-5-partes");
  await semViolacoes(page, "dividir-percentual-5-partes");

  await folha.getByLabel("Parte 1 (%)").fill("50");
  await expect(folha.getByText(/precisa ser 100%/)).toBeInViewport();
  await expect(folha.getByRole("button", { name: "Confirmar divisão" })).toBeInViewport();
  await shootNosTemas(page, "dividir-percentual-diferenca");

  await folha.getByRole("button", { name: "Valores livres" }).click();
  await expect(partes).toHaveCount(5);
  await folha.getByLabel("Parte 1", { exact: true }).fill("100");
  await expect(folha.getByText(/Faltam|Passou/)).toBeInViewport();
  await expect(folha.getByRole("button", { name: "Confirmar divisão" })).toBeInViewport();
  await shootNosTemas(page, "dividir-valores-5-partes");
  await semViolacoes(page, "dividir-valores-5-partes");
});

test("403 from the server on transfer, split and merge: told without a reason or a limit", async ({
  page,
  request,
  baseURL,
}) => {
  const s = sufixo();
  const a = await comanda(request, baseURL!, `Tres03 ${s}`, [{ codigo: "CHOPP", quantidade: 1 }]);
  await comanda(request, baseURL!, `Tres03B ${s}`);
  await entrarComo(page, "dono");
  await abrirDaLista(page, a.nome);
  for (const fim of ["itens/transferir", "juntar", "divisao"]) {
    await page.route(`**/comandas/*/${fim}`, (rota) => rota.fulfill({ status: 403, body: "" }));
  }

  await abrirMenu(page, "Transferir itens");
  const t = page.getByRole("dialog", { name: "Transferir itens" });
  await t.getByRole("checkbox", { name: /Chopp/ }).check();
  await t.getByRole("radio").first().check();
  await t.getByRole("button", { name: "Transferir", exact: true }).click();
  await expect(t.getByRole("alert")).toHaveText("Você não tem permissão para transferir itens.");
  await expect(t.getByRole("alert")).toBeInViewport();
  await expect(t).not.toContainText(/limite/i);
  await shootNosTemas(page, "transferir-403");
  await page.keyboard.press("Escape");

  await abrirMenu(page, "Juntar comandas");
  const j = page.getByRole("dialog", { name: "Juntar comandas" });
  await j.getByRole("radio").first().check();
  await j.getByRole("button", { name: "Continuar" }).click();
  await j.getByRole("button", { name: "Juntar comandas" }).click();
  await expect(j.getByRole("alert")).toHaveText("Você não tem permissão para juntar comandas.");
  await shootNosTemas(page, "juntar-403");
  await page.keyboard.press("Escape");

  await abrirMenu(page, "Dividir a conta");
  const d = page.getByRole("dialog", { name: "Dividir a conta" });
  await expect(d.getByRole("button", { name: "Confirmar divisão" })).toBeEnabled();
  await d.getByRole("button", { name: "Confirmar divisão" }).click();
  await expect(d.getByRole("alert")).toHaveText("Você não tem permissão para dividir a conta.");
  await expect(d.getByRole("alert")).toBeInViewport();
  await shootNosTemas(page, "dividir-403");
  await semViolacoes(page, "dividir-403");
});
