import { mkdirSync, writeFileSync } from "node:fs";
import { join } from "node:path";
import AxeBuilder from "@axe-core/playwright";
import { expect, type Page, test } from "@playwright/test";
import { lerSeed, SENHA } from "./seed";

/**
 * Accessibility scan of the rendered screens (F4-0109): axe-core over each screen, in light and dark, at both phone sizes.
 * STRICT: any WCAG 2.1 A/AA violation fails the test. Violations are also written to `e2e-results/axe/*.json`. The screens were clean when this was added;
 * `AXE_REPORT_ONLY=1` turns it into a report (annotation + JSON) while a new screen is being fixed.
 */
const saida = join(import.meta.dirname, "..", "e2e-results", "axe");
mkdirSync(saida, { recursive: true });

const seed = lerSeed();
const comApi = Boolean(process.env["E2E_API"]) && seed !== null;
const estrito = !process.env["AXE_REPORT_ONLY"];

async function varrer(page: Page, tela: string, tema: "light" | "dark") {
  await page.emulateMedia({ colorScheme: tema });
  // Buttons animate their background when the scheme flips; axe must read the colors at rest, not halfway through the transition.
  await page.addStyleTag({
    content: "*, *::before, *::after { transition: none !important; animation: none !important; }",
  });
  const size = page.viewportSize();
  const resultado = await new AxeBuilder({ page }).withTags(["wcag2a", "wcag2aa", "wcag21a", "wcag21aa"]).analyze();
  const resumo = resultado.violations.map((v) => ({
    regra: v.id,
    impacto: v.impact,
    ajuda: v.help,
    elementos: v.nodes.map((n) => n.target.join(" ")),
  }));
  writeFileSync(
    join(saida, `${tela}-${tema}-${size?.width ?? 0}x${size?.height ?? 0}.json`),
    JSON.stringify(resumo, null, 2),
  );
  if (resumo.length > 0) {
    test
      .info()
      .annotations.push({ type: "axe", description: `${tela}/${tema}: ${resumo.map((r) => r.regra).join(", ")}` });
  }
  if (estrito) {
    expect(resumo, `${tela}/${tema}`).toEqual([]);
  }
}

async function entrarComoCaixa(page: Page) {
  await page.goto("/salao/entrar");
  await page.getByLabel("Usuário ou e-mail").fill("caixa");
  await page.getByLabel("Código da organização").fill(seed!.organizacaoA);
  await page.getByLabel("Senha").fill(SENHA);
  await page.getByRole("button", { name: "Entrar" }).click();
  await expect(page.getByRole("heading", { level: 1, name: "Salão" })).toBeVisible();
}

test.describe("accessibility (axe-core)", () => {
  test("the scan is not vacuous: it reports a known violation", async ({ page }) => {
    await page.setContent('<main><img src="x.png"><button></button></main>');
    const resultado = await new AxeBuilder({ page }).withTags(["wcag2a"]).analyze();
    expect(resultado.violations.map((v) => v.id)).toEqual(expect.arrayContaining(["image-alt", "button-name"]));
  });

  test("sign-in screen", async ({ page }) => {
    await page.goto("/salao/entrar");
    await expect(page.getByRole("button", { name: "Entrar" })).toBeVisible();
    for (const tema of ["light", "dark"] as const) {
      await varrer(page, "entrar", tema);
    }
  });

  test.describe("signed in", () => {
    test.skip(!comApi, "needs the API running, the seed and E2E_API=1");

    test("comandas and caixa tabs", async ({ page }) => {
      await entrarComoCaixa(page);
      for (const tema of ["light", "dark"] as const) {
        await varrer(page, "comandas", tema);
      }

      await page.getByRole("link", { name: "Caixa" }).click();
      await expect(page.getByRole("heading", { level: 1, name: "Salão" })).toBeVisible();
      for (const tema of ["light", "dark"] as const) {
        await varrer(page, "caixa", tema);
      }
    });
  });
});
