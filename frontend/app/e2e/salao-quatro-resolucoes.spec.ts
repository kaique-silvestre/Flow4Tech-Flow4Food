import AxeBuilder from "@axe-core/playwright";
import { expect, test } from "@playwright/test";
import { semAnimacao, simularApi } from "./api-simulada";

/**
 * F4-0138: the critical flows of the comandas screen and the axe scan (strict, WCAG 2.1 A/AA, both themes) at the four resolutions of ADR-019:
 * 390x844, 360x740, 820x1180 and 1440x900. The API is answered inside the browser, so this runs with no backend.
 * Which flow runs where: the list and the open comanda (items, pre-bill, launching an item) run at all four; the side-by-side panel (tablet) and the shortcuts (computer)
 * stay in `salao-tablet.spec.ts` and `salao-computador.spec.ts`; the flows against the real API stay in the phone projects.
 */
test.use({ serviceWorkers: "block" });

const wcag = ["wcag2a", "wcag2aa", "wcag21a", "wcag21aa"];

for (const tema of ["light", "dark"] as const) {
  test.describe(`comandas at every resolution, ${tema}`, () => {
    test.beforeEach(async ({ page }) => {
      await page.emulateMedia({ colorScheme: tema });
      await simularApi(page);
    });

    test("the list shows the comandas and has no axe violation", async ({ page }) => {
      await page.goto("/salao");
      await expect(
        page.getByRole("list", { name: "Comandas abertas" }).getByRole("link", { name: /Ana/ }),
      ).toBeVisible();
      await expect(page.getByRole("group", { name: "Filtrar comandas" })).toBeVisible();
      expect(
        await page.evaluate(() => document.documentElement.scrollWidth <= document.documentElement.clientWidth),
      ).toBe(true);
      await semAnimacao(page);
      const resultado = await new AxeBuilder({ page }).withTags(wcag).analyze();
      expect(resultado.violations.map((v) => `${v.id}: ${v.nodes.map((n) => n.target.join(" ")).join(" | ")}`)).toEqual(
        [],
      );
    });

    test("opens a comanda, sees its items and the pre-bill action, adds an item to the round, no axe violation", async ({
      page,
    }) => {
      await page.goto("/salao/comanda/c7");
      await expect(page.getByRole("heading", { level: 2, name: "Ana" })).toBeVisible();
      await expect(page.getByText("Chopp 300ml").first()).toBeVisible();
      await expect(page.getByRole("button", { name: "Ver pré-conta" })).toBeVisible();
      expect(
        await page.evaluate(() => document.documentElement.scrollWidth <= document.documentElement.clientWidth),
      ).toBe(true);
      await page.getByRole("button", { name: "Adicionar Chopp 300ml" }).click();
      await expect(page.getByRole("button", { name: "Enviar", exact: true })).toBeVisible();
      await semAnimacao(page);
      const resultado = await new AxeBuilder({ page }).withTags(wcag).analyze();
      expect(resultado.violations.map((v) => `${v.id}: ${v.nodes.map((n) => n.target.join(" ")).join(" | ")}`)).toEqual(
        [],
      );
    });
  });
}
