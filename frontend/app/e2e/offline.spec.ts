import { mkdirSync } from "node:fs";
import { join } from "node:path";
import { type APIRequestContext, expect, type Page, test } from "@playwright/test";
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

async function novaComanda(page: Page, nome: string) {
  await page.getByRole("button", { name: "Nova comanda" }).click();
  await page.getByRole("dialog", { name: "Nova comanda" }).getByRole("button", { name: "Dar um nome" }).click();
  await page.getByLabel("Nome da comanda").fill(nome);
  await page.getByRole("dialog", { name: "Dar um nome" }).getByRole("button", { name: "OK" }).click();
  await expect(page.getByRole("heading", { level: 2, name: nome })).toBeVisible();
}

const mais = (page: Page, produto: string) => page.getByRole("button", { name: `Adicionar ${produto}` });

/** Another device: the same person signs in straight through the API (no page involved). */
async function apiComoGarcom2(request: APIRequestContext, baseURL: string) {
  const headers = { "X-F4T-Csrf": "1", Origin: baseURL };
  const login = await request.post("/login", {
    headers,
    data: { organizacaoDoLogin: seed!.organizacaoA, usuario: "garcom2", senha: SENHA },
  });
  expect(login.ok()).toBeTruthy();
  const { accessToken } = (await login.json()) as { accessToken: string };
  return { Authorization: `Bearer ${accessToken}`, ...headers };
}

interface ItemApi {
  rodadaId: string;
  posicao: number;
  produtoNome: string;
  quantidade: number;
}

async function itensDaComanda(request: APIRequestContext, auth: Record<string, string>, comandaId: string) {
  const r = await request.get(`/comandas/${comandaId}/itens`, { headers: auth });
  expect(r.ok()).toBeTruthy();
  return ((await r.json()) as { itens: ItemApi[] }).itens;
}

test("rounds sent without a connection wait in the queue and arrive once, in order, when the connection returns", async ({
  page,
  context,
  request,
  baseURL,
}) => {
  await page.emulateMedia({ reducedMotion: "reduce" });
  await entrarComoGarcom2(page);
  await novaComanda(page, `Offline ${Date.now() % 100000}`);
  const comandaId = /\/salao\/comanda\/([^/?#]+)/.exec(page.url())?.[1] ?? "";
  expect(comandaId).not.toBe("");

  // The connection drops; the screen says so and keeps working.
  await context.setOffline(true);
  await expect(page.getByText("Sem conexão. Você pode seguir lançando. Nada se perde.")).toBeVisible();
  await shoot(page, "13-sem-conexao");

  // First round: two chopps.
  await mais(page, "Chopp 300ml").click();
  await mais(page, "Chopp 300ml").click();
  await page.getByRole("button", { name: "Enviar", exact: true }).click();
  await page.getByRole("dialog", { name: "Enviar rodada" }).getByRole("button", { name: "Enviar agora" }).click();
  await expect(page.getByRole("button", { name: "1 ação pendente" })).toBeVisible();

  // Second round: one soda.
  await page
    .getByRole("tab", { name: "Bebidas" })
    .click()
    .catch(() => undefined);
  await mais(page, "Refrigerante").first().click();
  await page.getByRole("button", { name: "Enviar", exact: true }).click();
  await page.getByRole("dialog", { name: "Enviar rodada" }).getByRole("button", { name: "Enviar agora" }).click();
  const resumo = page.getByRole("button", { name: "2 ações pendentes" });
  await expect(resumo).toBeVisible();
  await resumo.click();
  await expect(page.getByRole("list", { name: "Ações pendentes" })).toBeVisible();
  await shoot(page, "13-fila-pendente");

  // Nothing reached the server while offline (read through another device).
  await context.setOffline(false);
  const auth = await apiComoGarcom2(request, baseURL!);

  // The queue drains by itself.
  await expect(page.getByRole("button", { name: /ações? pendentes?/ })).toHaveCount(0, { timeout: 60_000 });
  await shoot(page, "13-fila-enviada");

  const itens = await itensDaComanda(request, auth, comandaId);
  const rodadas = [...new Set(itens.map((i) => i.rodadaId))];
  expect(rodadas).toHaveLength(2);
  expect(itens).toHaveLength(2);
  const ordenados = [...itens].sort((a, b) => a.posicao - b.posicao);
  expect(ordenados.map((i) => [i.produtoNome, i.quantidade])).toEqual([
    ["Chopp 300ml", 2],
    [expect.stringMatching(/Refri/), 1],
  ]);
});

test("a comanda changed on another device while offline is adopted and the queued round still goes through once", async ({
  page,
  context,
  request,
  baseURL,
}) => {
  await entrarComoGarcom2(page);
  await novaComanda(page, `Conflito ${Date.now() % 100000}`);
  const comandaId = /\/salao\/comanda\/([^/?#]+)/.exec(page.url())?.[1] ?? "";

  await context.setOffline(true);
  await mais(page, "Chopp 300ml").click();
  await page.getByRole("button", { name: "Enviar", exact: true }).click();
  await page.getByRole("dialog", { name: "Enviar rodada" }).getByRole("button", { name: "Enviar agora" }).click();
  await expect(page.getByRole("button", { name: "1 ação pendente" })).toBeVisible();

  // Meanwhile another device launches a round (bumps the version).
  await context.setOffline(false);
  const auth = await apiComoGarcom2(request, baseURL!);
  const conta = await request.get(`/comandas/${comandaId}/conta`, { headers: auth });
  const { versao } = (await conta.json()) as { versao: number };
  const cardapio = await request.get("/cardapio/produtos", { headers: auth });
  const produtos = (await cardapio.json()) as
    | { id: string; nome: string }[]
    | { itens: { id: string; nome: string }[] };
  const lista = Array.isArray(produtos) ? produtos : produtos.itens;
  const agua = lista.find((p) => /gua/.test(p.nome)) ?? lista[0]!;
  const outra = await request.post(`/comandas/${comandaId}/rodadas`, {
    headers: auth,
    data: { rodadaId: crypto.randomUUID(), versao, itens: [{ produtoId: agua.id, quantidade: 1 }] },
  });
  expect(outra.status()).toBe(201);

  await expect(page.getByRole("button", { name: /ações? pendentes?/ })).toHaveCount(0, { timeout: 60_000 });
  const itens = await itensDaComanda(request, auth, comandaId);
  expect(itens).toHaveLength(2);
  expect(new Set(itens.map((i) => i.rodadaId)).size).toBe(2);
});
