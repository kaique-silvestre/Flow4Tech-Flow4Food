import { expect, test, type Page } from "@playwright/test";
import { mkdirSync } from "node:fs";
import { join } from "node:path";
import { lerSeed, SENHA } from "./seed";

const shots = join(import.meta.dirname, "..", "e2e-results", "screenshots");
mkdirSync(shots, { recursive: true });

async function shoot(page: Page, name: string) {
  const size = page.viewportSize();
  await page.screenshot({ path: join(shots, `${name}-${size?.width ?? 0}x${size?.height ?? 0}.png`), fullPage: false });
}

const seed = lerSeed();
const comApi = Boolean(process.env["E2E_API"]) && seed !== null;

/** The cashier of the seed holds the comanda and caixa permissions, so both tabs show. */
async function entrarComoCaixa(page: Page) {
  await page.goto("/salao/entrar");
  await page.getByLabel("Usuário ou e-mail").fill("caixa");
  await page.getByLabel("Código da organização").fill(seed!.organizacaoA);
  await page.getByLabel("Senha").fill(SENHA);
  await page.getByRole("button", { name: "Entrar" }).click();
  await expect(page.getByRole("heading", { level: 1, name: "Salão" })).toBeVisible();
}

test.describe("salão shell (signed in)", () => {
  test.skip(!comApi, "needs the API running, the seed and E2E_API=1");

  test("opens /salao with the title and the two tabs, in light and dark, and writes screenshots", async ({ page }) => {
    await page.emulateMedia({ colorScheme: "light" });
    await entrarComoCaixa(page);

    await expect(page.getByRole("heading", { level: 1, name: "Salão" })).toBeVisible();
    await expect(page.getByRole("link", { name: "Comandas" })).toHaveAttribute("aria-current", "page");
    await expect(page.getByRole("link", { name: "Caixa" })).toBeVisible();
    await shoot(page, "shell-light");

    await page.emulateMedia({ colorScheme: "dark" });
    await expect(page.getByRole("heading", { level: 1, name: "Salão" })).toBeVisible();
    await shoot(page, "shell-dark");
  });

  test("sends / to the sign-in when nobody is signed in, and to /salao when somebody is", async ({ page }) => {
    await page.goto("/");
    await expect(page).toHaveURL(/\/salao\/entrar$/);

    await entrarComoCaixa(page);
    await page.goto("/");

    await expect(page).toHaveURL(/\/salao$/);
  });

  test("has no horizontal scroll", async ({ page }) => {
    await entrarComoCaixa(page);
    await expect(page.getByRole("heading", { level: 1, name: "Salão" })).toBeVisible();

    const overflow = await page.evaluate(() => ({
      scroll: document.documentElement.scrollWidth,
      client: document.documentElement.clientWidth,
    }));

    expect(overflow.scroll).toBeLessThanOrEqual(overflow.client);
  });

  test("keeps every interactive element at least 44px high and wide", async ({ page }) => {
    await entrarComoCaixa(page);
    await expect(page.getByRole("link", { name: "Caixa" })).toBeVisible();

    const small = await page.evaluate(() =>
      [...document.querySelectorAll<HTMLElement>("a, button, input, select, textarea, [role=button], [role=tab]")]
        .map((el) => ({ text: el.textContent?.trim() ?? el.tagName, rect: el.getBoundingClientRect() }))
        .filter(({ rect }) => rect.width > 0 && (rect.width < 44 || rect.height < 44))
        .map(({ text, rect }) => `${text} ${Math.round(rect.width)}x${Math.round(rect.height)}`),
    );

    expect(small).toEqual([]);
  });

  test("shows the tab bar sticky at the bottom with the Caixa section reachable", async ({ page }) => {
    await entrarComoCaixa(page);

    await page.getByRole("link", { name: "Caixa" }).click();

    await expect(page).toHaveURL(/\/salao\/caixa$/);
    await expect(page.getByText("Caixa fechado. Abra o caixa para receber.").or(page.getByRole("heading", { level: 2, name: "Caixa aberto" }))).toBeVisible();
    const bar = await page.getByRole("navigation", { name: "Seções do salão" }).boundingBox();
    const viewport = page.viewportSize();
    expect(bar && viewport && Math.round(bar.y + bar.height)).toBe(viewport?.height);
  });
});

test.describe("installable PWA", () => {
  test("links a manifest that makes the app installable", async ({ page, request }) => {
    await page.goto("/salao");

    const href = await page.locator('link[rel="manifest"]').getAttribute("href");
    expect(href).toBeTruthy();
    const manifest = await (await request.get(href ?? "")).json();

    expect(manifest).toMatchObject({ name: "Flow4Tech Salão", short_name: "Salão", start_url: "/salao", display: "standalone" });
    const sizes = (manifest.icons as { sizes: string }[]).map((i) => i.sizes);
    expect(sizes).toEqual(expect.arrayContaining(["192x192", "512x512"]));
    for (const icon of manifest.icons as { src: string }[]) {
      const response = await request.get(icon.src);
      expect(response.status(), icon.src).toBe(200);
    }
  });

  test("registers a service worker that controls the page and serves the shell offline", async ({ page, context }) => {
    await page.goto("/salao");
    await page.evaluate(() => navigator.serviceWorker.ready.then(() => undefined));
    await page.reload();
    await expect.poll(() => page.evaluate(() => navigator.serviceWorker.controller !== null)).toBe(true);

    await context.setOffline(true);
    await page.goto("/salao");

    // The shell itself is served from the cache; with no network and no session it opens on the sign-in.
    await expect(page.getByRole("heading", { level: 1, name: "Entrar" })).toBeVisible();
    await context.setOffline(false);
  });
});

test.describe("real API through the app origin", () => {
  test.skip(!process.env["E2E_API"], "needs the API running (docker compose up, or dotnet run) and E2E_API=1");

  test("reaches the API health endpoint on the same origin", async ({ request }) => {
    const response = await request.get("/health/live");

    expect(response.status()).toBe(200);
  });
});
