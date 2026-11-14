import { mkdirSync } from "node:fs";
import { join } from "node:path";
import AxeBuilder from "@axe-core/playwright";
import { expect, type Page, test } from "@playwright/test";
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

/** Every shot is taken at rest: transitions off, an open sheet fully in place, both themes. */
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
      .filter((el) => el.closest("[aria-hidden=true]") === null)
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
    await page.addStyleTag({
      content: "*, *::before, *::after { transition: none !important; animation: none !important; }",
    });
    const r = await new AxeBuilder({ page }).withTags(["wcag2a", "wcag2aa", "wcag21a", "wcag21aa"]).analyze();
    expect(
      r.violations.map((v) => `${v.id}: ${v.nodes.map((n) => n.target.join(" ")).join(", ")}`),
      `${tela}/${tema}`,
    ).toEqual([]);
  }

  await page.emulateMedia({ colorScheme: "light" });
}

async function entrar(page: Page, usuario: string) {
  await page.goto("/salao/entrar");
  await page.getByLabel("Usuário ou e-mail").fill(usuario);
  if (!usuario.includes("@")) {
    await page.getByLabel("Código da organização").fill(seed!.organizacaoA);
  }

  await page.getByLabel("Senha").fill(SENHA);
  await page.getByRole("button", { name: "Entrar" }).click();
  await expect(page.getByRole("heading", { level: 1, name: "Salão" })).toBeVisible();
}

const CABECALHO = "tipo;codigo;nome;preco;categoria;grupo;minimo;maximo";
const csv = (...linhas: string[]) => Buffer.from([CABECALHO, ...linhas].join("\n"), "utf8");

async function enviar(page: Page, nome: string, conteudo: Buffer) {
  await page.getByLabel("Arquivo da planilha").setInputFiles({ name: nome, mimeType: "text/csv", buffer: conteudo });
}

for (const viewport of [
  { width: 390, height: 844 },
  { width: 360, height: 740 },
]) {
  test.describe(`${viewport.width}x${viewport.height}`, () => {
    test.use({ viewport });

    test("the owner downloads the template, reads the errors by line, fixes the file, imports, and a repeat changes nothing", async ({
      page,
    }) => {
      test.setTimeout(120_000);
      const codigo = `I${Date.now() % 1000000}`;
      await entrar(page, "dono@bar-a.test");
      await page.getByRole("link", { name: "Cardápio" }).click();
      await page.getByRole("link", { name: "Importar por planilha" }).click();
      await expect(page.getByRole("heading", { level: 2, name: "Importar cardápio" })).toBeVisible();
      await shoot(page, "70-importar-inicio");
      await semHorizontal(page);
      expect(await alvosPequenos(page)).toEqual([]);
      await axe(page, "importar");

      const [download] = await Promise.all([
        page.waitForEvent("download"),
        page.getByRole("button", { name: "Baixar modelo" }).click(),
      ]);
      expect(download.suggestedFilename()).toBe("modelo-cardapio.csv");

      // A wrong price: the report names the file line and the column, and nothing can be confirmed.
      await enviar(
        page,
        "erro.csv",
        csv("categoria;;Importados;;;;;", `produto;${codigo};Prato importado;abc;Importados;;;`),
      );
      await expect(page.getByText("Linha 3")).toBeVisible();
      await expect(page.getByRole("button", { name: "Confirmar importação" })).toHaveCount(0);
      await shoot(page, "70-importar-erros");
      await semHorizontal(page);
      expect(await alvosPequenos(page)).toEqual([]);

      // The fixed file previews with counts, then applies.
      await enviar(
        page,
        "ok.csv",
        csv("categoria;;Importados;;;;;", `produto;${codigo};Prato importado;12,50;Importados;;;`),
      );
      await expect(page.getByText("Pronta")).toBeVisible();
      await shoot(page, "70-importar-previa");
      await page.getByRole("button", { name: "Confirmar importação" }).click();
      await expect(page.getByRole("heading", { level: 2, name: "Cardápio importado" })).toBeVisible();
      await shoot(page, "70-importar-resultado");
      await semHorizontal(page);
      expect(await alvosPequenos(page)).toEqual([]);

      // The same file again has nothing to change.
      await page.getByRole("button", { name: "Importar outra planilha" }).click();
      await enviar(
        page,
        "ok.csv",
        csv("categoria;;Importados;;;;;", `produto;${codigo};Prato importado;12,50;Importados;;;`),
      );
      await expect(page.getByText(/Nada para mudar/)).toBeVisible();
      await shoot(page, "70-importar-sem-mudanca");

      await page.getByRole("link", { name: "Ver o cardápio" }).click();
      await expect(page.getByText("Prato importado")).toBeVisible();
    });

    test("offline at confirm keeps the file; the retry succeeds once the network is back", async ({
      page,
      context,
    }) => {
      test.setTimeout(120_000);
      const codigo = `O${Date.now() % 1000000}`;
      await entrar(page, "dono@bar-a.test");
      await page.goto("/salao/cardapio/importar");
      await enviar(
        page,
        "off.csv",
        csv("categoria;;Importados;;;;;", `produto;${codigo};Prato offline;9,90;Importados;;;`),
      );
      await expect(page.getByText("Pronta")).toBeVisible();
      await context.setOffline(true);
      await page.getByRole("button", { name: "Confirmar importação" }).click();
      await expect(page.getByText(/Sem conexão/)).toBeVisible();
      await shoot(page, "70-importar-offline");
      await context.setOffline(false);
      await page.getByRole("button", { name: "Tentar de novo" }).click();
      await expect(page.getByRole("heading", { level: 2, name: "Cardápio importado" })).toBeVisible();
    });

    test("who may not import is told so, without a reason", async ({ page }) => {
      await entrar(page, "garcom");
      await page.goto("/salao/cardapio/importar");
      await expect(page.getByText("Você não tem permissão para isso.")).toBeVisible();
      await shoot(page, "70-importar-proibido");
    });
  });
}
