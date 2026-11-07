import { mkdirSync } from "node:fs";
import { join } from "node:path";
import { type APIRequestContext, expect, type Page, test } from "@playwright/test";
import { lerSeed, SENHA } from "./seed";

const shots = join(import.meta.dirname, "..", "e2e-results", "screenshots");
mkdirSync(shots, { recursive: true });

const seed = lerSeed();
test.skip(
  !process.env["E2E_API"] || seed === null,
  "needs the API running, the seed (frontend/app/e2e-results/seed.json) and E2E_API=1",
);

async function shoot(page: Page, name: string) {
  const size = page.viewportSize();
  await page.screenshot({ path: join(shots, `${name}-${size?.width ?? 0}x${size?.height ?? 0}.png`) });
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
      .map((el) => ({
        nome: el.textContent?.trim() || el.getAttribute("aria-label") || el.tagName,
        r: el.getBoundingClientRect(),
      }))
      .filter(({ r }) => r.width > 0 && (r.width < 44 || r.height < 44))
      .map(({ nome, r }) => `${nome} ${Math.round(r.width)}x${Math.round(r.height)}`),
  );
}

async function entrarComo(page: Page, usuario: string, senha: string) {
  await page.goto("/salao/entrar");
  await page.getByLabel("Usuário ou e-mail").fill(usuario);
  await page.getByLabel("Código da organização").fill(seed!.organizacaoA);
  await page.getByLabel("Senha").fill(senha);
  await page.getByRole("button", { name: "Entrar" }).click();
}

/** The owner asks the real route for a fresh temporary credential of an operational account (the seed made 'novato'). */
async function credencialTemporaria(request: APIRequestContext, baseURL: string): Promise<string> {
  const cabecalhos = { "X-F4T-Csrf": "1", Origin: baseURL };
  const login = await request.post("/login", { headers: cabecalhos, data: { email: "dono@bar-a.test", senha: SENHA } });
  expect(login.status()).toBe(200);
  const { accessToken } = (await login.json()) as { accessToken: string };
  const r = await request.post(`/contas/${seed!.contas["novato"]}/redefinir-acesso`, {
    headers: { ...cabecalhos, Authorization: `Bearer ${accessToken}` },
  });
  expect(r.status()).toBe(200);
  return ((await r.json()) as { credencial: string }).credencial;
}

test.describe("sign in", () => {
  test("shows one generic failure for a wrong password", async ({ page }) => {
    await page.emulateMedia({ colorScheme: "light" });
    await page.goto("/salao");
    await expect(page).toHaveURL(/\/salao\/entrar$/);
    await shoot(page, "09-entrar-vazio");
    await page.emulateMedia({ colorScheme: "dark" });
    await shoot(page, "09-entrar-vazio-escuro");
    await page.emulateMedia({ colorScheme: "light" });

    await entrarComo(page, "garcom", "senha-errada-errada");

    await expect(page.getByRole("alert")).toHaveText("Usuário ou senha inválidos.");
    await expect(page.getByLabel("Senha")).toHaveValue("");
    await shoot(page, "09-entrar-erro");
    await semHorizontal(page);
    expect(await alvosPequenos(page)).toEqual([]);
  });

  test("the waiter signs in and lands on the empty comanda list, and a reload keeps the session", async ({ page }) => {
    await entrarComo(page, "garcom", SENHA);

    await expect(page).toHaveURL(/\/salao$/);
    await expect(page.getByText("Nenhuma comanda aberta. Toque em Nova comanda.")).toBeVisible();
    await shoot(page, "09-comandas-vazia");
    await semHorizontal(page);
    expect(await alvosPequenos(page)).toEqual([]);

    await page.reload();
    await expect(page.getByText("Nenhuma comanda aberta. Toque em Nova comanda.")).toBeVisible();

    await page.getByRole("button", { name: "Sair" }).click();
    await expect(page).toHaveURL(/\/salao\/entrar$/);
    await page.goto("/salao");
    await expect(page).toHaveURL(/\/salao\/entrar$/);
  });

  test("the e-mail sign-in needs no organization", async ({ page }) => {
    await page.goto("/salao/entrar");
    await page.getByLabel("Usuário ou e-mail").fill("dono@bar-a.test");
    await expect(page.getByLabel("Código da organização")).toHaveCount(0);
    await page.getByLabel("Senha").fill(SENHA);
    await page.getByRole("button", { name: "Entrar" }).click();

    await expect(page).toHaveURL(/\/salao$/);
  });

  test("shows the blocked and the verification states (answers forced at the network edge: producing them for real would lock the test address)", async ({
    page,
  }) => {
    await page.route("**/login", (route) => route.fulfill({ status: 429, headers: { "Retry-After": "90" } }));
    await entrarComo(page, "garcom", "qualquer-senha-longa");

    await expect(page.getByRole("status")).toContainText("Tente de novo em 1:");
    await expect(page.getByRole("button", { name: "Entrar" })).toBeDisabled();
    await shoot(page, "09-entrar-bloqueado");
    await semHorizontal(page);

    await page.unroute("**/login");
    await page.route("**/login", (route) => route.fulfill({ status: 428 }));
    await page.reload();
    await entrarComo(page, "garcom", "qualquer-senha-longa");

    await expect(page.getByText("Verificação necessária")).toBeVisible();
    await shoot(page, "09-entrar-verificacao");
  });
});

test.describe("temporary password", () => {
  test("goes straight to the password change, shows the policy inline and then lands on the list", async ({
    page,
    request,
    baseURL,
  }) => {
    const temporaria = await credencialTemporaria(request, baseURL!);

    await entrarComo(page, "novato", temporaria);

    await expect(page).toHaveURL(/\/salao\/trocar-senha$/);
    await expect(page.getByRole("heading", { level: 1, name: "Escolha sua senha" })).toBeVisible();
    await shoot(page, "09-trocar-senha");
    await page.goto("/salao");
    await expect(page).toHaveURL(/\/salao\/trocar-senha$/);

    await page.getByLabel("Senha provisória").fill(temporaria);
    await page.getByLabel("Nova senha", { exact: true }).fill("curta");
    await page.getByLabel("Repita a nova senha").fill("curta");
    await page.getByRole("button", { name: "Salvar senha" }).click();
    await expect(page.getByText("Use pelo menos 10 caracteres.", { exact: true }).first()).toBeVisible();
    await shoot(page, "09-trocar-senha-erro");
    await semHorizontal(page);
    expect(await alvosPequenos(page)).toEqual([]);

    const nova = `nova senha do novato ${Date.now()}`;
    await page.getByLabel("Nova senha", { exact: true }).fill(nova);
    await page.getByLabel("Repita a nova senha").fill(nova);
    await page.getByRole("button", { name: "Salvar senha" }).click();

    await expect(page).toHaveURL(/\/salao$/);
    await expect(page.getByText("Nenhuma comanda aberta. Toque em Nova comanda.")).toBeVisible();
  });

  test("a wrong temporary password is refused on its own field", async ({ page, request, baseURL }) => {
    const temporaria = await credencialTemporaria(request, baseURL!);
    await entrarComo(page, "novato", temporaria);
    await expect(page).toHaveURL(/\/salao\/trocar-senha$/);

    await page.getByLabel("Senha provisória").fill("provisoria-que-nao-e-a-certa");
    await page.getByLabel("Nova senha", { exact: true }).fill("uma senha nova bem longa");
    await page.getByLabel("Repita a nova senha").fill("uma senha nova bem longa");
    await page.getByRole("button", { name: "Salvar senha" }).click();

    await expect(page.getByText("Senha provisória incorreta.")).toBeVisible();
  });
});

test.describe("unit", () => {
  test("a user with two units chooses one and lands on the list", async ({ page }) => {
    await entrarComo(page, "gerente", SENHA);

    await expect(page).toHaveURL(/\/salao\/unidade$/);
    await expect(page.getByRole("heading", { level: 1, name: "Escolha a unidade" })).toBeVisible();
    await expect(page.getByRole("button", { name: "Centro" })).toBeVisible();
    await shoot(page, "09-unidade");
    await semHorizontal(page);
    expect(await alvosPequenos(page)).toEqual([]);
    await page.goto("/salao");
    await expect(page).toHaveURL(/\/salao\/unidade$/);

    await page.getByRole("button", { name: "Praia" }).click();

    await expect(page).toHaveURL(/\/salao$/);
    await expect(page.getByText("Nenhuma comanda aberta. Toque em Nova comanda.")).toBeVisible();
  });

  test("a user with no unit sees the explanation and can sign out", async ({ page }) => {
    await entrarComo(page, "semunidade", SENHA);

    await expect(page.getByRole("heading", { level: 1, name: "Sem unidade" })).toBeVisible();
    await shoot(page, "09-sem-unidade");
    await page.getByRole("button", { name: "Sair" }).click();

    await expect(page).toHaveURL(/\/salao\/entrar$/);
  });
});
