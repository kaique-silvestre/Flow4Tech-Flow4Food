import { appendFileSync, mkdirSync } from "node:fs";
import { join } from "node:path";
import { type APIRequestContext, expect, type Page, type Route, test } from "@playwright/test";
import { lerSeed, SENHA } from "./seed";

const shots = join(import.meta.dirname, "..", "e2e-results", "screenshots");
mkdirSync(shots, { recursive: true });

const seed = lerSeed();
test.skip(!process.env["E2E_API"] || seed === null, "needs the API running, the seed and E2E_API=1");

/** Every shot is taken at rest: transitions off and an open sheet fully in place, never halfway through its slide. */
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

async function entrarComoGarcom2(page: Page) {
  await page.goto("/salao/entrar");
  await page.getByLabel("Usuário ou e-mail").fill("garcom2");
  await page.getByLabel("Código da organização").fill(seed?.organizacaoA ?? "");
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

const mais = (page: Page, produto: string) => page.getByRole("button", { name: `Adicionar ${produto}` });

async function semHorizontal(page: Page) {
  const m = await page.evaluate(() => ({
    scroll: document.documentElement.scrollWidth,
    client: document.documentElement.clientWidth,
  }));
  expect(m.scroll).toBeLessThanOrEqual(m.client);
}

/** A word is never cut in the middle: no text node of the screen is wider than its box without wrapping or an ellipsis it chose. */
async function semTextoCortado(page: Page) {
  const cortados = await page.evaluate(() =>
    [...document.querySelectorAll<HTMLElement>(".ultima-vez *, .enviar-linha *, .picker-secao, .comanda-barra *")]
      .filter((el) => el.scrollWidth > el.clientWidth + 1 && getComputedStyle(el).overflow !== "visible")
      .map((el) => `${el.className} ${el.textContent?.slice(0, 30)}`),
  );
  expect(cortados).toEqual([]);
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

function meta(page: Page, texto: string) {
  const tela = page.viewportSize();
  appendFileSync(
    join(shots, "..", "metas.txt"),
    `${new Date().toISOString()} ${tela?.width}x${tela?.height} ${texto} (F4-0053)\n`,
  );
}

interface Sessao {
  headers: Record<string, string>;
}

async function login(request: APIRequestContext, baseURL: string, usuario: string): Promise<Sessao> {
  const headers = { "X-F4T-Csrf": "1", Origin: baseURL };
  const r = await request.post("/login", {
    headers,
    data: { organizacaoDoLogin: seed?.organizacaoA, usuario, senha: SENHA },
  });
  expect(r.ok()).toBeTruthy();
  return {
    headers: { ...headers, Authorization: `Bearer ${((await r.json()) as { accessToken: string }).accessToken}` },
  };
}

interface MesaComHistorico {
  mesaId: string;
  identificacao: string;
  /** Opens a fresh comanda on the table (as the waiter), so the screen shows the card. */
  abrirComanda: () => Promise<string>;
  fechar: (comandaId: string) => Promise<void>;
}

/**
 * Straight through the API (other devices): a free table gets a comanda with 2 chopps, a portion of fries and a note, the cashier receives it and closes it. That leaves the "last time at this
 * table" history; the waiter then opens a new comanda on the same table.
 */
async function mesaComHistorico(request: APIRequestContext, baseURL: string): Promise<MesaComHistorico> {
  const garcom = await login(request, baseURL, "garcom2");
  const caixa = await login(request, baseURL, "caixa");
  // The register must be open for the cashier to receive (409 when it already is: fine).
  await request.post("/caixa/turnos", { headers: caixa.headers, data: { fundoCentavos: 0 } });

  const mesas = (await (await request.get("/mesas", { headers: garcom.headers })).json()) as {
    id: string;
    identificacao: string;
    status: string;
  }[];
  const livre = mesas.find((m) => m.status === "livre");
  expect(livre, "a free table in the seed").toBeTruthy();
  const mesa = livre as { id: string; identificacao: string };

  const abrir = async () => {
    const r = await request.post("/comandas", {
      headers: garcom.headers,
      data: { identificacao: null, mesaId: mesa.id, pessoas: null },
    });
    expect(r.ok(), `abrir comanda na mesa: ${r.status()} ${await r.text()}`).toBeTruthy();
    return (await r.json()) as { id: string; versao: number };
  };
  const fechar = async (comandaId: string) => {
    const conta = (await (await request.get(`/comandas/${comandaId}/conta`, { headers: caixa.headers })).json()) as {
      totalCentavos: number;
      versao: number;
    };
    let versao = Number(conta.versao);
    if (Number(conta.totalCentavos) > 0) {
      const pago = await request.post(`/comandas/${comandaId}/pagamentos`, {
        headers: caixa.headers,
        data: { pagamentoId: crypto.randomUUID(), recebidoCentavos: Number(conta.totalCentavos), versao },
      });
      expect(pago.ok()).toBeTruthy();
      versao = Number(((await pago.json()) as { comandaVersao: number }).comandaVersao);
    }

    const fechada = await request.post(`/comandas/${comandaId}/fechar`, { headers: caixa.headers, data: { versao } });
    expect(fechada.ok()).toBeTruthy();
  };

  const antiga = await abrir();
  const produtos = (await (await request.get("/cardapio/produtos", { headers: garcom.headers })).json()) as {
    id: string;
    codigo: string;
  }[];
  const id = (codigo: string) => (produtos.find((p) => p.codigo === codigo) as { id: string }).id;
  const rodada = await request.post(`/comandas/${antiga.id}/rodadas`, {
    headers: garcom.headers,
    data: {
      rodadaId: crypto.randomUUID(),
      versao: antiga.versao,
      itens: [
        { produtoId: id("CHOPP"), quantidade: 2, observacao: null },
        { produtoId: id("BATATA"), quantidade: 1, observacao: "sem sal" },
      ],
    },
  });
  expect(rodada.status()).toBe(201);
  await fechar(antiga.id);

  return {
    mesaId: mesa.id,
    identificacao: mesa.identificacao,
    abrirComanda: async () => (await abrir()).id,
    fechar,
  };
}

/** Lets a request through to the real API and rewrites its JSON answer. */
async function reescrever(route: Route, mudar: (corpo: unknown) => unknown) {
  const resposta = await route.fetch();
  await route.fulfill({ response: resposta, json: mudar(await resposta.json()) });
}

async function enviarRodada(page: Page) {
  await page.getByRole("button", { name: "Enviar", exact: true }).click();
  await page.getByRole("dialog", { name: "Enviar rodada" }).getByRole("button", { name: "Enviar agora" }).click();
  await expect(page.getByText("Rodada enviada", { exact: false })).toBeVisible();
}

test("favorites and most sold are on top, a code plus Enter launches, and the measured targets are written", async ({
  page,
  request,
  baseURL,
}) => {
  // Feeds the favorites of this waiter (the launch of the history is theirs).
  await mesaComHistorico(request, baseURL as string);
  await entrarComoGarcom2(page);

  const inicio = Date.now();
  await novaComanda(page, `Rapido ${Date.now() % 100000}`);
  // The favorites tab is the first and the open one: the common item is one tap away, no tab to change.
  await expect(page.getByRole("tab", { name: "Favoritos" })).toHaveAttribute("aria-selected", "true");
  await expect(page.getByRole("heading", { level: 3, name: "Seus favoritos" })).toBeVisible();
  await mais(page, "Chopp 300ml").click();
  await enviarRodada(page);
  const gasto = Date.now() - inicio;
  expect(gasto).toBeLessThan(10_000);
  meta(
    page,
    `abrir comanda e primeiro item enviado em ${gasto} ms (limite 10000); item comum a partir de Favoritos: 1 toque para entrar na rodada, 3 toques ate o envio (adicionar, Enviar, Enviar agora)`,
  );
  await shootNosTemas(page, "53-favoritos");
  await semHorizontal(page);
  await semTextoCortado(page);
  expect(await alvosPequenos(page)).toEqual([]);

  // Search by code: type the exact short code and press Enter, the product goes into the round and the field is clear.
  const busca = page.getByRole("searchbox", { name: "Buscar produto" });
  await busca.fill("batata");
  await expect(mais(page, "Porção de batata")).toBeVisible();
  await busca.press("Enter");
  await expect(page.getByRole("region", { name: "Rodada não enviada" })).toContainText("1 item não enviado");
  await expect(busca).toHaveValue("");

  // A name with accent missing finds the product; the result is the server's (exact code first).
  await busca.fill("porcao");
  await expect(mais(page, "Porção de batata")).toBeVisible();
  await shootNosTemas(page, "53-busca-resultado");
  await semTextoCortado(page);

  // The numeric keypad toggle for a code.
  await page.getByRole("button", { name: "123, teclado numérico para o código" }).click();
  await expect(busca).toHaveAttribute("inputmode", "numeric");
  await page.getByRole("button", { name: "123, teclado numérico para o código" }).click();

  // Nothing found: a clear message.
  await busca.fill("xyz");
  await expect(page.getByText("Nada com “xyz”. Tente o código do produto.")).toBeVisible();
  await shootNosTemas(page, "53-busca-vazia");

  // No signal for the server search: the loaded menu answers (the field keeps working).
  await page.route("**/cardapio/produtos/busca*", (route) => route.abort());
  await busca.fill("chopp");
  await expect(mais(page, "Chopp 300ml")).toBeVisible();
  await shoot(page, "53-busca-sem-servidor");
  await page.unroute("**/cardapio/produtos/busca*");
});

test("repeating the last round is two taps with a preview, and what cannot go is named", async ({ page }) => {
  await entrarComoGarcom2(page);
  await novaComanda(page, `Repetir ${Date.now() % 100000}`);
  await mais(page, "Chopp 300ml").click();
  await mais(page, "Chopp 300ml").click();
  await page.getByRole("tab", { name: "Porções" }).click();
  await mais(page, "Porção de batata").click();
  await enviarRodada(page);
  const totalAntes = await page.getByLabel("Total da conta").textContent();

  // Tap 1: the preview. Tap 2: send.
  await page.getByRole("button", { name: "Repetir última rodada" }).click();
  const folha = page.getByRole("dialog", { name: "Repetir última rodada" });
  await expect(folha.getByRole("list", { name: "Itens que vão na rodada" })).toContainText("Chopp 300ml");
  await expect(folha.getByRole("list", { name: "Itens que vão na rodada" })).toContainText("Porção de batata");
  await expect(folha).toContainText("Total estimado");
  await shootNosTemas(page, "53-repetir-previa");
  await semTextoCortado(page);
  await folha.getByRole("button", { name: "Repetir agora" }).click();
  await expect(page.getByText("Rodada enviada", { exact: false })).toBeVisible();
  await expect(page.getByRole("dialog", { name: "Repetir última rodada" })).toHaveCount(0);
  meta(page, "repetir ultima rodada: 2 toques (abrir a previa, Repetir agora)");
  const totalDepois = await page.getByLabel("Total da conta").textContent();
  expect(totalDepois).not.toBe(totalAntes);
  await shoot(page, "53-repetir-enviada");
  await semHorizontal(page);

  // The repeat can be undone inside the window, like any round.
  await page.getByRole("button", { name: "Desfazer" }).click();
  await expect(page.getByText("Rodada desfeita. Os itens saíram da conta.")).toBeVisible();
  await expect(page.getByLabel("Total da conta")).toHaveText(totalAntes ?? "");
});

test("the repeat sheet in every state: a product that is out, a conflict, forbidden, an error, loading and offline", async ({
  page,
  context,
}) => {
  await entrarComoGarcom2(page);
  await novaComanda(page, `Estados ${Date.now() % 100000}`);
  await mais(page, "Chopp 300ml").click();
  await page.getByRole("tab", { name: "Porções" }).click();
  await mais(page, "Porção de batata").click();
  await enviarRodada(page);
  await expect(page.getByRole("button", { name: "Desfazer" })).toHaveCount(0, { timeout: 12_000 });

  const abrir = async () => {
    await page.getByRole("button", { name: "Repetir última rodada" }).click();
    return page.getByRole("dialog", { name: "Repetir última rodada" });
  };
  const fechar = async () => {
    await page.getByRole("dialog", { name: "Repetir última rodada" }).getByRole("button", { name: "Voltar" }).click();
    await expect(page.getByRole("dialog")).toHaveCount(0);
  };

  // A product that ran out since the last round: the preview names it under "Não entram".
  await page.route("**/cardapio/produtos", (route) =>
    route.request().method() === "GET"
      ? reescrever(route, (corpo) =>
          (corpo as { codigo: string }[]).map((p) => (p.codigo === "BATATA" ? { ...p, indisponivel: true } : p)),
        )
      : route.continue(),
  );
  await page.reload();
  await expect(page.getByRole("button", { name: "Repetir última rodada" })).toBeEnabled();
  let folha = await abrir();
  await expect(folha.getByRole("group", { name: "Itens que não entram" })).toContainText("Porção de batata");
  await expect(folha.getByRole("list", { name: "Itens que vão na rodada" })).toContainText("Chopp 300ml");
  await shootNosTemas(page, "53-repetir-nao-entram");
  await semTextoCortado(page);
  await fechar();
  await page.unroute("**/cardapio/produtos");
  await page.reload();
  await expect(page.getByRole("button", { name: "Repetir última rodada" })).toBeEnabled();

  // 409 conflict: another device changed the comanda; the bill refreshes and the sheet stays.
  await page.route("**/rodadas/repetir", (route) =>
    route.fulfill({ status: 409, json: { codigo: "versao-desatualizada", versaoAtual: 99, ignorados: [] } }),
  );
  folha = await abrir();
  await folha.getByRole("button", { name: "Repetir agora" }).click();
  await expect(folha.getByText(/foi alterada em outro aparelho/)).toBeVisible();
  await shootNosTemas(page, "53-repetir-conflito");
  await page.unroute("**/rodadas/repetir");
  // The invented version of the mock is forgotten with a reload (the real server names the real one).
  await page.reload();
  await expect(page.getByRole("button", { name: "Repetir última rodada" })).toBeEnabled();
  folha = await abrir();

  // 403 with no reason.
  await page.route("**/rodadas/repetir", (route) => route.fulfill({ status: 403 }));
  await folha.getByRole("button", { name: "Repetir agora" }).click();
  await expect(folha.getByText("Você não tem permissão para lançar itens.")).toBeVisible();
  await shoot(page, "53-repetir-proibido");
  await page.unroute("**/rodadas/repetir");

  // Nothing left to repeat (409 with the list of what was out).
  await page.route("**/rodadas/repetir", (route) =>
    route.fulfill({
      status: 409,
      json: {
        codigo: "nada-para-repetir",
        versaoAtual: null,
        ignorados: [
          { produtoId: crypto.randomUUID(), produtoNome: "Chopp 300ml", quantidade: 1, motivo: "produto-inativo" },
        ],
      },
    }),
  );
  await folha.getByRole("button", { name: "Repetir agora" }).click();
  await expect(folha.getByText(/Nenhum item da última rodada está disponível agora\./)).toBeVisible();
  await shoot(page, "53-repetir-nada");
  await page.unroute("**/rodadas/repetir");

  // 400: a plain error, the sheet stays.
  await page.route("**/rodadas/repetir", (route) => route.fulfill({ status: 400, json: { codigo: "invalido" } }));
  await folha.getByRole("button", { name: "Repetir agora" }).click();
  await expect(folha.getByText("Não foi possível repetir a rodada. Tente de novo.")).toBeVisible();
  await shoot(page, "53-repetir-erro");
  await page.unroute("**/rodadas/repetir");

  // Loading: the answer takes a while and the button shows it.
  await page.route("**/rodadas/repetir", async (route) => {
    await new Promise((r) => setTimeout(r, 2500));
    await route.continue();
  });
  const total = await page.getByLabel("Total da conta").textContent();
  await folha.getByRole("button", { name: "Repetir agora" }).click();
  await expect(folha.getByRole("button", { name: "Repetir agora" })).toBeDisabled();
  await shoot(page, "53-repetir-carregando");
  await expect(page.getByText("Rodada enviada", { exact: false })).toBeVisible({ timeout: 8000 });
  await page.unroute("**/rodadas/repetir");
  await expect(page.getByLabel("Total da conta")).not.toHaveText(total ?? "");
  await expect(page.getByRole("button", { name: "Desfazer" })).toHaveCount(0, { timeout: 12_000 });

  // Offline: the repeat waits in the visible queue and goes out by itself when the signal returns.
  const totalOffline = await page.getByLabel("Total da conta").textContent();
  folha = await abrir();
  await context.setOffline(true);
  await folha.getByRole("button", { name: "Repetir agora" }).click();
  await expect(page.getByText(/A repetição ficou na fila/)).toBeVisible();
  await page.getByRole("button", { name: "1 ação pendente" }).click();
  await expect(page.getByText(/^Repetir rodada/).first()).toBeVisible();
  await expect(page.getByRole("button", { name: "Repetir última rodada" })).toBeDisabled();
  await shootNosTemas(page, "53-repetir-na-fila");
  await semHorizontal(page);
  await context.setOffline(false);
  await expect(page.getByLabel("Total da conta")).not.toHaveText(totalOffline ?? "", { timeout: 20_000 });
});

test("the last-time card shows only when the server has history, adds everything in one tap and names what ran out", async ({
  page,
  request,
  baseURL,
}) => {
  const mesa = await mesaComHistorico(request, baseURL as string);
  const comandaId = await mesa.abrirComanda();
  try {
    await entrarComoGarcom2(page);
    await page.getByText(`Mesa ${mesa.identificacao}`).first().click();
    await expect(page.getByRole("heading", { level: 2, name: `Mesa ${mesa.identificacao}` })).toBeVisible();

    const cartao = page.getByRole("region", { name: "Última vez nesta mesa" });
    await expect(cartao).toBeVisible();
    await expect(cartao).toContainText("2× Chopp 300ml, 1× Porção de batata");
    await expect(cartao).toContainText("fechada hoje");
    await shootNosTemas(page, "53-ultima-vez");
    await semHorizontal(page);
    await semTextoCortado(page);
    expect(await alvosPequenos(page)).toEqual([]);

    await cartao.getByRole("button", { name: "Adicionar tudo da última vez nesta mesa" }).click();
    await expect(page.getByRole("region", { name: "Rodada não enviada" })).toContainText("3 itens não enviados");
    await expect(cartao).toHaveCount(0);
    meta(page, "ultima vez nesta mesa: 1 toque para trazer a rodada inteira");
    await shoot(page, "53-ultima-vez-adicionada");

    // The server has nothing for the table: no card, no hole in the screen.
    await page.route("**/mesas/*/ultima-vez", (route) =>
      route.fulfill({ json: { comandaId: null, numero: null, numeroTexto: null, fechadaEm: null, itens: [] } }),
    );
    await page.reload();
    await expect(page.getByRole("heading", { level: 2, name: `Mesa ${mesa.identificacao}` })).toBeVisible();
    await expect(page.getByRole("tab", { name: "Favoritos" })).toBeVisible();
    await expect(page.getByRole("region", { name: "Última vez nesta mesa" })).toHaveCount(0);
    await shoot(page, "53-ultima-vez-sem-historico");

    // Forbidden (403 with no reason) is also just no card.
    await page.unroute("**/mesas/*/ultima-vez");
    await page.route("**/mesas/*/ultima-vez", (route) => route.fulfill({ status: 403 }));
    await page.reload();
    await expect(page.getByRole("tab", { name: "Favoritos" })).toBeVisible();
    await expect(page.getByRole("region", { name: "Última vez nesta mesa" })).toHaveCount(0);
    await page.unroute("**/mesas/*/ultima-vez");
  } finally {
    await mesa.fechar(comandaId);
  }
});

test("the picker states: loading, error with retry, and no favorites yet", async ({ page }) => {
  await entrarComoGarcom2(page);
  await novaComanda(page, `Picker ${Date.now() % 100000}`);

  // Loading: the menu is slow.
  await page.route("**/cardapio/produtos", async (route) => {
    if (route.request().method() !== "GET") {
      return route.continue();
    }

    await new Promise((r) => setTimeout(r, 2500));
    return route.continue();
  });
  await page.reload();
  await expect(page.getByLabel("Carregando o cardápio")).toBeVisible();
  await shootNosTemas(page, "53-picker-carregando");
  await expect(page.getByRole("tab", { name: "Favoritos" })).toBeVisible({ timeout: 8000 });
  await page.unroute("**/cardapio/produtos");

  // Error: the menu does not load; retry brings it back.
  let falhar = true;
  await page.route("**/cardapio/produtos", (route) =>
    route.request().method() === "GET" && falhar ? route.fulfill({ status: 500 }) : route.continue(),
  );
  await page.reload();
  await expect(page.getByText("Não foi possível carregar o cardápio.")).toBeVisible();
  await shootNosTemas(page, "53-picker-erro");
  falhar = false;
  await page.getByRole("button", { name: "Tentar novamente" }).click();
  await expect(page.getByRole("tab", { name: "Favoritos" })).toBeVisible();
  await page.unroute("**/cardapio/produtos");

  // Empty: neither favorites nor most sold yet.
  await page.route("**/cardapio/produtos/favoritos", (route) => route.fulfill({ json: [] }));
  await page.route("**/cardapio/produtos/frequentes", (route) => route.fulfill({ json: [] }));
  await page.reload();
  await expect(page.getByRole("tab", { name: "Bebidas" })).toHaveAttribute("aria-selected", "true");
  await page.getByRole("tab", { name: "Favoritos" }).click();
  await expect(page.getByText("Ainda não há favoritos. Use a busca ou uma categoria.")).toBeVisible();
  await shootNosTemas(page, "53-picker-sem-favoritos");
  await semHorizontal(page);
});
