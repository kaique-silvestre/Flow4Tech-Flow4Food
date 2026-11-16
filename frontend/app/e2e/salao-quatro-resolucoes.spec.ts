import { mkdirSync } from "node:fs";
import { join } from "node:path";
import AxeBuilder from "@axe-core/playwright";
import { expect, test } from "@playwright/test";
import { comanda, semAnimacao, simularApi, simularFluxo } from "./api-simulada";

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

test.describe("comandas list updates live over the stream (F4-0140)", () => {
  test("a comanda changed by someone else shows its new version with no reload", async ({ page }) => {
    await simularApi(page);
    let consumo = 2500;
    let versao = 2;
    const fluxo = await simularFluxo(page, () => [{ ...comanda(7, "Ana", consumo), versao }, comanda(8, "Bia", 8900)]);
    await page.goto("/salao");
    const ana = page.getByRole("list", { name: "Comandas abertas" }).getByRole("link", { name: /Ana/ });
    await expect(ana).toContainText("R$ 25,00");
    await page.evaluate(() => {
      (window as unknown as { marca: string }).marca = "mesma-pagina";
    });

    consumo = 4800;
    versao = 3;
    fluxo.emitir(
      "comanda",
      {
        eventoId: "e1",
        tipo: "comanda.rodada-enviada",
        em: new Date().toISOString(),
        comandas: [{ comandaId: "c7", versao: 3 }],
      },
      "e1",
    );

    await expect(ana).toContainText("R$ 48,00", { timeout: 3000 });
    expect(await page.evaluate(() => (window as unknown as { marca?: string }).marca)).toBe("mesma-pagina");
    await expect(page.getByText(/Reconectando ao vivo/)).toHaveCount(0);
  });
});

const capturas = join(import.meta.dirname, "..", "..", "..", ".scratch", "S-05-gestao-web-e-telas", "shots", "F4-0140");

for (const tema of ["light", "dark"] as const) {
  test(`the reconnecting notice shows when the stream drops, ${tema}`, async ({ page }) => {
    await page.emulateMedia({ colorScheme: tema });
    await simularApi(page);
    const fluxo = await simularFluxo(page, () => [comanda(7, "Ana", 2500), comanda(8, "Bia", 8900)]);
    fluxo.derrubar(true);
    await page.goto("/salao");
    const aviso = page.getByText(/Reconectando ao vivo/);
    await expect(aviso).toBeVisible();
    await expect(page.getByRole("list", { name: "Comandas abertas" }).getByRole("link", { name: /Ana/ })).toBeVisible();
    expect(
      await page.evaluate(() => document.documentElement.scrollWidth <= document.documentElement.clientWidth),
    ).toBe(true);
    const barra = page.getByRole("navigation").last();
    if (await barra.isVisible()) {
      const a = await aviso.boundingBox();
      const b = await barra.boundingBox();
      if (a && b) {
        expect(a.y + a.height <= b.y || b.y + b.height <= a.y || a.x + a.width <= b.x || b.x + b.width <= a.x).toBe(
          true,
        );
      }
    }

    await semAnimacao(page);
    mkdirSync(capturas, { recursive: true });
    const t = page.viewportSize();
    await page.screenshot({ path: join(capturas, `reconectando-${tema}-${t?.width}x${t?.height}.png`) });

    fluxo.derrubar(false);
    await expect(aviso).toHaveCount(0, { timeout: 10_000 });
  });
}
