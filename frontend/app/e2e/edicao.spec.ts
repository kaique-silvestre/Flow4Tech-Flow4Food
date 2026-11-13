import { mkdirSync } from "node:fs";
import { join } from "node:path";
import { type APIRequestContext, expect, type Page, test } from "@playwright/test";
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

async function entrarComoDono(page: Page) {
  await page.goto("/salao/entrar");
  await page.getByLabel("Usuário ou e-mail").fill("dono@bar-a.test");
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
  return { id: comanda.id, versao: ((await rodada.json()) as { comandaVersao: number }).comandaVersao, auth };
}

async function abrirDaLista(page: Page, nome: string) {
  await page.getByRole("link", { name: new RegExp(nome) }).click();
  await expect(page.getByRole("heading", { level: 2, name: nome })).toBeVisible();
}

async function abrirMenu(page: Page, acao: string) {
  await page.getByRole("button", { name: "Mais" }).click();
  await page.getByRole("button", { name: acao, exact: true }).click();
}

test("edit the item and the comanda, read the history, hit a conflict and cancel it, in both themes", async ({
  page,
  request,
  baseURL,
}) => {
  const nome = `Edição ${Date.now() % 100000}`;
  await comandaDoGarcom(request, baseURL!, nome);
  await entrarComoDono(page);
  await abrirDaLista(page, nome);
  await expect(page.getByLabel("Total da conta")).toContainText("27,50");

  // The item: one more chopp and a note. The edit goes through the queue and the bill follows.
  await page.getByRole("button", { name: "Alterar Chopp 300ml" }).click();
  const folhaItem = page.getByRole("dialog", { name: "Alterar item" });
  await folhaItem.getByRole("button", { name: "Aumentar Chopp 300ml" }).click();
  await folhaItem.getByLabel("Observação").fill("sem espuma");
  await shootNosTemas(page, "edicao-item");
  await folhaItem.getByRole("button", { name: "Salvar" }).click();
  await expect(page.getByLabel("Total da conta")).toContainText("41,25");
  await expect(page.getByText("sem espuma")).toBeVisible();

  // The name, the head count and the note.
  await abrirMenu(page, "Renomear comanda");
  await page.getByLabel("Nome da comanda").fill(`${nome} 2`);
  await page.getByRole("dialog", { name: "Renomear comanda" }).getByRole("button", { name: "Salvar" }).click();
  await expect(page.getByRole("heading", { level: 2, name: `${nome} 2` })).toBeVisible();
  await abrirMenu(page, "Pessoas e observação");
  await page.getByLabel("Número de pessoas").fill("4");
  await page.getByLabel("Observação da comanda").fill("aniversário");
  await page.getByRole("dialog", { name: "Pessoas e observação" }).getByRole("button", { name: "Salvar" }).click();

  // The menu and the history.
  await page.getByRole("button", { name: "Mais" }).click();
  await expect(page.getByRole("list", { name: "Ações da comanda" }).getByRole("button")).toHaveCount(6);
  await shootNosTemas(page, "edicao-menu");
  await page.getByRole("button", { name: "Histórico", exact: true }).click();
  const historico = page.getByRole("list", { name: "Linha do tempo da comanda" });
  await expect(historico.getByRole("listitem").filter({ hasText: "Comanda aberta" })).toBeVisible();
  await expect(historico.getByRole("listitem").filter({ hasText: "Item alterado" })).toBeVisible();
  await expect(historico.getByRole("listitem").filter({ hasText: "Comanda renomeada" })).toBeVisible();
  await expect(historico.getByRole("listitem").filter({ hasText: "Dados alterados" })).toBeVisible();
  await semHorizontal(page);
  await shootNosTemas(page, "edicao-historico");
  await page.getByRole("dialog", { name: "Histórico" }).getByRole("button", { name: "Fechar" }).click();

  // Cancel: reason, then an explicit confirmation.
  await abrirMenu(page, "Cancelar comanda");
  const cancelar = page.getByRole("dialog", { name: "Cancelar comanda" });
  await cancelar.getByRole("button", { name: "Erro de lançamento" }).click();
  await shootNosTemas(page, "edicao-cancelar-motivo");
  await cancelar.getByRole("button", { name: "Continuar" }).click();
  const confirmar = page.getByRole("dialog", { name: /Cancelar a comanda/ });
  await shootNosTemas(page, "edicao-cancelar-confirmar");
  await confirmar.getByRole("button", { name: "Cancelar comanda" }).click();
  await expect(page.getByRole("heading", { level: 1, name: "Salão" })).toBeVisible();
  await expect(page.getByRole("link", { name: new RegExp(`${nome} 2`) })).toHaveCount(0);
});

test("a stale version on cancel is told and refreshes the bill", async ({ page, request, baseURL }) => {
  const nome = `Conflito ${Date.now() % 100000}`;
  const comanda = await comandaDoGarcom(request, baseURL!, nome);
  await entrarComoDono(page);
  await abrirDaLista(page, nome);
  await expect(page.getByLabel("Total da conta")).toContainText("27,50");

  // The waiter (another device) renames the comanda: the owner's screen still holds the old version.
  const renomeada = await request.post(`/comandas/${comanda.id}/renomear`, {
    headers: comanda.auth,
    data: { versao: comanda.versao, identificacao: `${nome} b` },
  });
  expect(renomeada.ok()).toBeTruthy();

  await abrirMenu(page, "Cancelar comanda");
  const cancelar = page.getByRole("dialog", { name: "Cancelar comanda" });
  await cancelar.getByRole("button", { name: "Cliente desistiu" }).click();
  await cancelar.getByRole("button", { name: "Continuar" }).click();
  await page
    .getByRole("dialog", { name: /Cancelar a comanda/ })
    .getByRole("button", { name: "Cancelar comanda" })
    .click();
  await expect(page.getByText(/foi alterada em outro aparelho/)).toBeVisible();
  await shootNosTemas(page, "edicao-conflito");

  await page
    .getByRole("dialog", { name: /Cancelar a comanda/ })
    .getByRole("button", { name: "Cancelar comanda" })
    .click();
  await expect(page.getByRole("heading", { level: 1, name: "Salão" })).toBeVisible();
});
