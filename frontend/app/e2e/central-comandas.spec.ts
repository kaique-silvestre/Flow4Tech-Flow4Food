import { mkdirSync } from "node:fs";
import { join } from "node:path";
import AxeBuilder from "@axe-core/playwright";
import { expect, type Page, test } from "@playwright/test";
import { semAnimacao, simularApi } from "./api-simulada";

/**
 * F4-0142: the central de comandas at /gestao/comandas on the computer (1440x900), cards and table, filtered by waiter. The API is answered inside the browser (no backend).
 * Visual reference: .scratch/S-00-novo-sistema/prototipos/index.html#m=web and #m=web&view=table.
 */
test.use({ serviceWorkers: "block" });

const shots = join(import.meta.dirname, "..", "..", "..", ".scratch", "S-05-gestao-web-e-telas", "shots", "F4-0142");

async function shoot(page: Page, name: string) {
  mkdirSync(shots, { recursive: true });
  await page.screenshot({ path: join(shots, `${name}-1440x900.png`) });
}

async function axe(page: Page) {
  const resultado = await new AxeBuilder({ page }).withTags(["wcag2a", "wcag2aa", "wcag21a", "wcag21aa"]).analyze();
  expect(resultado.violations.map((v) => `${v.id}: ${v.nodes.map((n) => n.target.join(" ")).join(" | ")}`)).toEqual([]);
}

for (const tema of ["light", "dark"] as const) {
  test.describe(`central de comandas, ${tema}`, () => {
    test.beforeEach(async ({ page }) => {
      test.skip(page.viewportSize()?.width !== 1440, "computer project only");
      await page.emulateMedia({ colorScheme: tema });
      await simularApi(page);
    });

    test("filters by waiter, the total changes, the table shows the same rows, axe strict", async ({ page }) => {
      await page.goto("/gestao/comandas");

      await expect(page.getByRole("status").filter({ hasText: "comandas abertas" })).toHaveText("3 comandas abertas");
      await expect(page.getByRole("link", { name: "Central" })).toHaveAttribute("aria-current", "page");
      await expect(page.getByRole("list", { name: "Comandas abertas" }).getByRole("link")).toHaveCount(3);
      await semAnimacao(page);
      await shoot(page, `central-cartoes-${tema}`);
      await axe(page);

      await page.getByRole("combobox", { name: "Garçom" }).selectOption({ label: "Rafael" });
      await expect(page.getByRole("status").filter({ hasText: "comanda aberta" })).toHaveText("1 comanda aberta");
      await expect(page.getByRole("list", { name: "Comandas abertas" }).getByRole("link")).toHaveCount(1);

      await page.getByRole("combobox", { name: "Garçom" }).selectOption({ label: "Todos" });
      await page.getByRole("button", { name: "Tabela" }).click();
      const tabela = page.getByRole("table", { name: "Comandas abertas" });
      await expect(tabela.getByRole("row")).toHaveCount(4);
      await expect(page).toHaveURL(/vista=tabela/);
      await expect(page.getByRole("link", { name: "Central" })).toHaveAttribute("aria-current", "page");
      await shoot(page, `central-tabela-${tema}`);
      await axe(page);

      // Keyboard focus rings: a toggle button, then a link of the table.
      await page.keyboard.press("Tab");
      await page.getByRole("button", { name: "Cartões" }).focus();
      await expect(page.getByRole("button", { name: "Cartões" })).toBeFocused();
      await shoot(page, `central-foco-alternador-${tema}`);
      await tabela.getByRole("link").first().focus();
      await shoot(page, `central-foco-link-${tema}`);
    });

    test("shows the empty state of a filter and clears it", async ({ page }) => {
      await page.goto("/gestao/comandas");
      await page.getByRole("textbox", { name: "Número" }).fill("99");
      await page.keyboard.press("Enter");

      await expect(page.getByText("Nenhuma comanda aberta com estes filtros.")).toBeVisible();
      await expect(page.getByRole("link", { name: "Central" })).toHaveAttribute("aria-current", "page");
      await shoot(page, `central-vazio-${tema}`);
      await page.getByRole("button", { name: "Limpar filtros" }).click();
      await expect(page.getByRole("list", { name: "Comandas abertas" }).getByRole("link")).toHaveCount(3);
    });
  });
}
