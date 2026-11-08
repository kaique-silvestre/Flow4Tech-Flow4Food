import { appendFileSync, mkdirSync } from "node:fs";
import { join } from "node:path";
import AxeBuilder from "@axe-core/playwright";
import { type APIRequestContext, expect, type Page, test } from "@playwright/test";
import { lerSeed, SENHA } from "./seed";

const shots = join(import.meta.dirname, "..", "e2e-results", "screenshots");
mkdirSync(shots, { recursive: true });

const seed = lerSeed();
test.use({
  locale: "pt-BR",
  launchOptions: {
    args: ["--no-sandbox", "--lang=pt-BR"],
    ...(process.env["PW_CHROMIUM"] ? { executablePath: process.env["PW_CHROMIUM"] } : {}),
  },
});
test.skip(!process.env["E2E_API"] || seed === null, "needs the API running, the seed and E2E_API=1");

/** Every shot is taken at rest (transitions off, the sheet fully in place), in both themes. */
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
      .filter((el) => el.closest("[aria-hidden=true]") === null && !el.closest("[data-aria-hidden=true]"))
      .map((el) => ({
        nome: el.textContent?.trim() || el.getAttribute("aria-label") || el.tagName,
        r: el.getBoundingClientRect(),
      }))
      .filter(({ r }) => r.width > 0 && (r.width < 44 || r.height < 44))
      .map(({ nome, r }) => `${nome} ${Math.round(r.width)}x${Math.round(r.height)}`),
  );
}

async function axe(page: Page, tela: string) {
  for (const tema of ["light", "dark"] as const) {
    await page.emulateMedia({ colorScheme: tema });
    const r = await new AxeBuilder({ page }).withTags(["wcag2a", "wcag2aa", "wcag21a", "wcag21aa"]).analyze();
    expect(
      r.violations.map((v) => `${v.id}: ${v.nodes.map((n) => n.target.join(" ")).join(", ")}`),
      `${tela}/${tema}`,
    ).toEqual([]);
  }

  await page.emulateMedia({ colorScheme: "light" });
}

async function entrarComoGarcom2(page: Page) {
  await page.goto("/salao/entrar");
  await page.getByLabel("Usuário ou e-mail").fill("garcom2");
  await page.getByLabel("Código da organização").fill(seed!.organizacaoA);
  await page.getByLabel("Senha").fill(SENHA);
  await page.getByRole("button", { name: "Entrar" }).click();
  await expect(page.getByRole("heading", { level: 2, name: "Minhas comandas" })).toBeVisible();
}

async function novaComanda(page: Page, nome: string) {
  await page.getByRole("button", { name: "Nova comanda" }).click();
  await page.getByRole("dialog", { name: "Nova comanda" }).getByRole("button", { name: "Dar um nome" }).click();
  await page.getByLabel("Nome da comanda").fill(nome);
  await page.getByRole("dialog", { name: "Dar um nome" }).getByRole("button", { name: "OK" }).click();
  await expect(page.getByRole("heading", { level: 2, name: nome })).toBeVisible();
}

async function logar(request: APIRequestContext, baseURL: string, dados: Record<string, string>) {
  const headers = { "X-F4T-Csrf": "1", Origin: baseURL };
  const login = await request.post("/login", { headers, data: dados });
  expect(login.ok()).toBeTruthy();
  const { accessToken } = (await login.json()) as { accessToken: string };
  return { Authorization: `Bearer ${accessToken}`, ...headers };
}

interface Cenario {
  nome: string;
  codigo: string;
  produtoId: string;
  opcoes: Record<string, string>;
}

/** The owner's menu through the real API: a product with a required group (Ponto) and an optional one (Extras); a prior launch with Bacon makes it the most used add-on. */
async function criarProdutoComOpcoes(request: APIRequestContext, baseURL: string): Promise<Cenario> {
  const dono = await logar(request, baseURL, { email: "dono@bar-a.test", senha: SENHA });
  const sufixo = String(Date.now() % 1_000_000);
  const categorias = (await (await request.get("/cardapio/categorias", { headers: dono })).json()) as { id: string }[];
  const nome = `Hambúrguer ${sufixo}`;
  const codigo = `HB${sufixo}`;
  const produto = await request.post("/cardapio/produtos", {
    headers: dono,
    data: { codigo, nome, precoCentavos: 3000, categoriaId: categorias[0]!.id, ativo: true, indisponivel: false },
  });
  expect(produto.ok()).toBeTruthy();
  const { id: produtoId } = (await produto.json()) as { id: string };

  const ponto = await request.post("/cardapio/grupos-opcao", {
    headers: dono,
    data: {
      nome: `Ponto ${sufixo}`,
      minimo: 1,
      maximo: 1,
      ativo: true,
      opcoes: [
        { id: null, nome: "Mal passado", precoCentavos: 0, ativa: true },
        { id: null, nome: "Ao ponto", precoCentavos: 0, ativa: true },
      ],
    },
  });
  expect(ponto.ok()).toBeTruthy();
  const extras = await request.post("/cardapio/grupos-opcao", {
    headers: dono,
    data: {
      nome: `Extras ${sufixo}`,
      minimo: 0,
      maximo: 2,
      ativo: true,
      opcoes: [
        { id: null, nome: "Bacon", precoCentavos: 500, ativa: true },
        { id: null, nome: "Queijo", precoCentavos: 400, ativa: true },
      ],
    },
  });
  expect(extras.ok()).toBeTruthy();
  const gPonto = (await ponto.json()) as { id: string; opcoes: { id: string; nome: string }[] };
  const gExtras = (await extras.json()) as { id: string; opcoes: { id: string; nome: string }[] };
  const vinculo = await request.put(`/cardapio/produtos/${produtoId}/grupos-opcao`, {
    headers: dono,
    data: { grupoIds: [gPonto.id, gExtras.id] },
  });
  expect(vinculo.ok()).toBeTruthy();

  const opcoes: Record<string, string> = {};
  for (const o of [...gPonto.opcoes, ...gExtras.opcoes]) {
    opcoes[o.nome] = o.id;
  }

  // A launch with Bacon (as another waiter would): it becomes the most used add-on of the product.
  const garcom = await logar(request, baseURL, {
    organizacaoDoLogin: seed!.organizacaoA,
    usuario: "garcom2",
    senha: SENHA,
  });
  const comanda = await request.post("/comandas", {
    headers: garcom,
    data: { identificacao: `Base ${sufixo}`, mesaId: null, pessoas: null },
  });
  expect(comanda.ok()).toBeTruthy();
  const { id: comandaId, versao } = (await comanda.json()) as { id: string; versao: number };
  const lancou = await request.post(`/comandas/${comandaId}/rodadas`, {
    headers: garcom,
    data: {
      rodadaId: crypto.randomUUID(),
      versao,
      itens: [{ produtoId, quantidade: 1, observacao: null, opcaoIds: [opcoes["Ao ponto"], opcoes["Bacon"]] }],
    },
  });
  expect(lancou.status()).toBe(201);
  return { nome, codigo, produtoId, opcoes };
}

async function buscar(page: Page, codigo: string) {
  await page.getByRole("searchbox", { name: "Buscar produto" }).fill(codigo);
}

interface ItemApi {
  rodadaId: string;
  produtoNome: string;
  quantidade: number;
  observacao: string | null;
  totalCentavos: number;
  opcoes: { nome: string }[];
}

async function itensDaComanda(request: APIRequestContext, baseURL: string, comandaId: string) {
  const garcom = await logar(request, baseURL, {
    organizacaoDoLogin: seed!.organizacaoA,
    usuario: "garcom2",
    senha: SENHA,
  });
  const r = await request.get(`/comandas/${comandaId}/itens`, { headers: garcom });
  expect(r.ok()).toBeTruthy();
  return ((await r.json()) as { itens: ItemApi[] }).itens;
}

const idDaComanda = (page: Page) => /\/salao\/comanda\/([^/?#]+)/.exec(page.url())?.[1] ?? "";

test("a product with a required group opens the sheet: most used chip, keypad quantity, note; the bill matches the server", async ({
  page,
  request,
  baseURL,
}) => {
  test.setTimeout(120_000);
  const c = await criarProdutoComOpcoes(request, baseURL!);
  await entrarComoGarcom2(page);
  await novaComanda(page, `Opções ${Date.now() % 100000}`);
  await buscar(page, c.codigo);
  await expect(page.getByRole("button", { name: `Opções de ${c.nome}` })).toBeVisible();
  await shoot(page, "51-grade-com-opcoes");

  await page.getByRole("button", { name: `Adicionar ${c.nome}` }).click();
  const folha = page.getByRole("dialog", { name: c.nome });
  await expect(folha).toBeVisible();
  const adicionar = folha.getByRole("button", { name: /^Adicionar/ });
  await expect(adicionar).toBeDisabled();
  const usados = folha.getByRole("group", { name: "Mais usados" });
  await expect(usados).toBeVisible();
  await expect(usados.getByRole("button", { name: /Bacon/ })).toBeVisible();
  await shoot(page, "51-folha-obrigatorio");
  await semHorizontal(page);

  // One tap on the most used add-on, one on the required group.
  await usados.getByRole("button", { name: /Bacon/ }).click();
  await expect(usados.getByRole("button", { name: /Bacon/ })).toHaveAttribute("aria-pressed", "true");
  await folha.getByRole("group", { name: "Mais usados" }).getByRole("button", { name: "Ao ponto" }).click();
  await folha.getByLabel("Observação").fill("sem cebola");
  await folha.getByRole("button", { name: "Teclado" }).click();
  await folha.getByRole("group", { name: "Teclado numérico" }).getByRole("button", { name: "2" }).click();
  await expect(adicionar).toContainText("70,00");
  // With the keypad open the primary action and Voltar stay inside the screen (pinned footer), on one line.
  await expect(adicionar).toBeInViewport({ ratio: 1 });
  await expect(folha.getByRole("button", { name: "Voltar" })).toBeInViewport({ ratio: 1 });
  expect((await adicionar.boundingBox())!.height).toBeLessThan(60);
  await shoot(page, "51-folha-preenchida");
  expect(await alvosPequenos(page)).toEqual([]);
  await axe(page, "folha-do-item");
  await adicionar.click();
  await expect(folha).toHaveCount(0);

  const linha = page.getByRole("group", { name: c.nome }).locator("xpath=ancestor::li");
  await expect(linha).toContainText("2×");
  await expect(linha).toContainText("Bacon");
  await expect(linha).toContainText("sem cebola");
  await expect(linha).toContainText("70,00");
  await shoot(page, "51-rodada-com-opcoes");

  await page.getByRole("button", { name: "Enviar", exact: true }).click();
  const enviar = page.getByRole("dialog", { name: "Enviar rodada" });
  await expect(enviar).toContainText("Bacon, Ao ponto");
  await expect(enviar).toContainText("70,00");
  await shoot(page, "51-enviar-com-opcoes");
  await enviar.getByRole("button", { name: "Enviar agora" }).click();
  await expect(page.getByText("Rodada enviada", { exact: false })).toBeVisible();

  // The bill shows the same total the server computed ((30,00 + 5,00) x 2).
  await expect(page.getByLabel("Total da conta")).toContainText(/7[07],00/);
  const itens = await itensDaComanda(request, baseURL!, idDaComanda(page));
  expect(itens).toHaveLength(1);
  expect(itens[0]).toMatchObject({ quantidade: 2, observacao: "sem cebola", totalCentavos: 7000 });
  expect(itens[0]?.opcoes.map((o) => o.nome).sort()).toEqual(["Ao ponto", "Bacon"]);
  await shoot(page, "51-conta-com-opcoes");
});

test("a product with no options still goes into the round in one tap, with no sheet; a note can be added to any line", async ({
  page,
}) => {
  await entrarComoGarcom2(page);
  await novaComanda(page, `Simples ${Date.now() % 100000}`);

  await buscar(page, "chopp");
  await page.getByRole("button", { name: "Adicionar Chopp 300ml" }).click();
  await expect(page.getByRole("dialog")).toHaveCount(0);
  await expect(page.getByRole("region", { name: "Rodada não enviada" })).toContainText("1 item não enviado");
  // The round bar is pinned just above the tab bar even with a short list.
  const barra = (await page.getByRole("region", { name: "Rodada não enviada" }).boundingBox())!;
  const abas = (await page.getByRole("navigation", { name: "Seções do salão" }).boundingBox())!;
  expect(Math.abs(barra.y + barra.height - abas.y)).toBeLessThan(4);
  // Taps from the signed-in comanda screen to the server: add (1), Enviar (2), Enviar agora (3: the confirmation sheet of the existing flow).
  const tela = page.viewportSize();
  appendFileSync(
    join(shots, "..", "metas.txt"),
    `${new Date().toISOString()} ${tela?.width}x${tela?.height} produto sem opcoes: 1 toque entra na rodada, sem folha de opcoes (F4-0051)\n`,
  );

  await page.getByRole("button", { name: "Ajustar Chopp 300ml" }).click();
  const folha = page.getByRole("dialog", { name: "Chopp 300ml" });
  await expect(folha.getByRole("group", { name: "Mais usados" })).toHaveCount(0);
  await folha.getByLabel("Observação").fill("sem gelo");
  await shoot(page, "51-folha-so-observacao");
  await folha.getByRole("button", { name: /^Salvar/ }).click();
  await expect(page.getByRole("region", { name: "Rodada não enviada" })).toBeVisible();
  await expect(page.getByRole("group", { name: "Chopp 300ml" }).locator("xpath=ancestor::li")).toContainText(
    "sem gelo",
  );
  await shoot(page, "51-rodada-com-observacao");
  await semHorizontal(page);
});

test("offline, the round with add-ons waits in the queue and reconnecting creates exactly one round", async ({
  page,
  context,
  request,
  baseURL,
}) => {
  test.setTimeout(120_000);
  const c = await criarProdutoComOpcoes(request, baseURL!);
  await page.emulateMedia({ reducedMotion: "reduce" });
  await entrarComoGarcom2(page);
  await novaComanda(page, `Fila ${Date.now() % 100000}`);
  const comandaId = idDaComanda(page);
  await buscar(page, c.codigo);
  await expect(page.getByRole("button", { name: `Opções de ${c.nome}` })).toBeVisible();

  await context.setOffline(true);
  await expect(page.getByText("Sem conexão. Você pode seguir lançando. Nada se perde.")).toBeVisible();
  await page.getByRole("button", { name: `Adicionar ${c.nome}` }).click();
  const folha = page.getByRole("dialog", { name: c.nome });
  await folha
    .getByRole("group", { name: /^Ponto/ })
    .getByRole("button", { name: "Mal passado" })
    .click();
  await folha
    .getByRole("group", { name: /^Extras/ })
    .getByRole("button", { name: /Queijo/ })
    .click();
  await folha.getByLabel("Observação").fill("bem quente");
  await folha.getByRole("button", { name: /^Adicionar/ }).click();
  await page.getByRole("button", { name: "Enviar", exact: true }).click();
  await page.getByRole("dialog", { name: "Enviar rodada" }).getByRole("button", { name: "Enviar agora" }).click();
  const pendente = page.getByRole("button", { name: "1 ação pendente" });
  await expect(pendente).toBeVisible();
  await pendente.click();
  await expect(page.getByRole("list", { name: "Ações pendentes" })).toBeVisible();
  await shoot(page, "51-fila-com-opcoes");

  await context.setOffline(false);
  await expect(page.getByRole("button", { name: /ações? pendentes?/ })).toHaveCount(0, { timeout: 60_000 });
  const itens = await itensDaComanda(request, baseURL!, comandaId);
  expect(itens).toHaveLength(1);
  expect(new Set(itens.map((i) => i.rodadaId)).size).toBe(1);
  expect(itens[0]).toMatchObject({ observacao: "bem quente", totalCentavos: 3400 });
  expect(itens[0]?.opcoes.map((o) => o.nome).sort()).toEqual(["Mal passado", "Queijo"]);
});

test("loading, rejected add-on and stale version are told in words without losing the staged round", async ({
  page,
  request,
  baseURL,
}) => {
  test.setTimeout(120_000);
  const c = await criarProdutoComOpcoes(request, baseURL!);
  await entrarComoGarcom2(page);
  await novaComanda(page, `Erros ${Date.now() % 100000}`);
  await buscar(page, c.codigo);
  await expect(page.getByRole("button", { name: `Opções de ${c.nome}` })).toBeVisible();

  // Loading: the most used are slow to come.
  let soltar: () => void = () => undefined;
  const segurada = new Promise<void>((resolve) => {
    soltar = resolve;
  });
  await page.route(`**/cardapio/produtos/${c.produtoId}/opcoes-frequentes`, async (rota) => {
    await segurada;
    await rota.continue();
  });
  await page.getByRole("button", { name: `Adicionar ${c.nome}` }).click();
  const folha = page.getByRole("dialog", { name: c.nome });
  await expect(folha.getByRole("status", { name: "Carregando os mais usados" })).toBeVisible();
  await shoot(page, "51-folha-carregando");
  soltar();
  await expect(folha.getByRole("group", { name: "Mais usados" })).toBeVisible();
  await folha.getByRole("group", { name: "Mais usados" }).getByRole("button", { name: "Ao ponto" }).click();
  await folha.getByRole("button", { name: /^Adicionar/ }).click();

  // Error: the server says an add-on changed.
  await page.route(`**/comandas/${idDaComanda(page)}/rodadas`, (rota) =>
    rota.fulfill({ status: 409, contentType: "application/json", body: JSON.stringify({ codigo: "opcao-inativa" }) }),
  );
  await page.getByRole("button", { name: "Enviar", exact: true }).click();
  const enviar = page.getByRole("dialog", { name: "Enviar rodada" });
  await enviar.getByRole("button", { name: "Enviar agora" }).click();
  await expect(
    enviar.getByText("Os adicionais de um item mudaram. Volte, ajuste o item e envie de novo."),
  ).toBeVisible();
  await shoot(page, "51-erro-opcao-mudou");
  await enviar.getByRole("button", { name: "Voltar" }).click();
  await expect(page.getByRole("region", { name: "Rodada não enviada" })).toBeVisible();

  // Conflict: another device changed the comanda.
  await page.unroute(`**/comandas/${idDaComanda(page)}/rodadas`);
  await page.route(
    `**/comandas/${idDaComanda(page)}/rodadas`,
    (rota) =>
      rota.fulfill({
        status: 409,
        contentType: "application/json",
        body: JSON.stringify({ codigo: "versao-desatualizada", versaoAtual: 9 }),
      }),
    { times: 1 },
  );
  await page.getByRole("button", { name: "Enviar", exact: true }).click();
  await page.getByRole("dialog", { name: "Enviar rodada" }).getByRole("button", { name: "Enviar agora" }).click();
  await expect(page.getByText(/foi alterada em outro aparelho/)).toBeVisible();
  await expect(page.getByRole("region", { name: "Rodada não enviada" })).toBeVisible();
  await shoot(page, "51-conflito-versao");
});
