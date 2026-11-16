import { mkdirSync } from "node:fs";
import { join } from "node:path";
import AxeBuilder from "@axe-core/playwright";
import { expect, type Page, test } from "@playwright/test";
import { semAnimacao, simularApi } from "./api-simulada";

/**
 * F4-0137: on the computer (1440x900) the sidebar, the list and the open comanda sit side by side, and the keyboard works on the comandas screen only.
 * The API is answered inside the browser, so this runs with no backend. Visual reference: .scratch/S-00-novo-sistema/prototipos/index.html#m=web&open=3.
 */
test.use({ serviceWorkers: "block" });

const shots = join(import.meta.dirname, "..", "..", "..", ".scratch", "S-05-gestao-web-e-telas", "shots", "F4-0137");

const computador = (page: Page) => page.viewportSize()?.width === 1440;

/** The F4-0137 captures are committed and approved: they are only rewritten on purpose (`CAPTURAS=1`). */
async function shoot(page: Page, name: string) {
  if (!process.env["CAPTURAS"]) {
    return;
  }

  mkdirSync(shots, { recursive: true });
  await page.screenshot({ path: join(shots, `${name}-1440x900.png`) });
}

async function axe(page: Page) {
  const resultado = await new AxeBuilder({ page }).withTags(["wcag2a", "wcag2aa", "wcag21a", "wcag21aa"]).analyze();
  expect(resultado.violations.map((v) => `${v.id}: ${v.nodes.map((n) => n.target.join(" ")).join(" | ")}`)).toEqual([]);
}

const larguraDe = async (page: Page, seletor: string) =>
  Math.round((await page.locator(seletor).first().boundingBox())?.width ?? 0);

for (const tema of ["light", "dark"] as const) {
  test.describe(`salão on the computer, ${tema}`, () => {
    test.beforeEach(async ({ page }) => {
      test.skip(!computador(page), "computer project only");
      await page.emulateMedia({ colorScheme: tema });
      await simularApi(page);
    });

    test("shows the 264 px menu, the list and the 420 px comanda panel side by side, axe strict", async ({ page }) => {
      await page.goto("/salao/comanda/c7");

      const lista = page.getByRole("list", { name: "Comandas abertas" });
      const painel = page.getByRole("region", { name: "Comanda aberta" });
      await expect(painel.getByRole("heading", { level: 2, name: "Ana" })).toBeVisible();
      await expect(lista).toBeVisible();
      expect(await larguraDe(page, ".app-lateral")).toBe(264);
      expect(Math.round((await painel.boundingBox())?.width ?? 0)).toBe(420);
      const [caixaLista, caixaPainel] = await Promise.all([lista.boundingBox(), painel.boundingBox()]);
      expect(caixaLista && caixaPainel && caixaLista.x + caixaLista.width <= caixaPainel.x).toBe(true);
      expect(
        await page.evaluate(() => document.documentElement.scrollWidth <= document.documentElement.clientWidth),
      ).toBe(true);
      await semAnimacao(page);
      await shoot(page, `computador-comanda-${tema}`);
      await axe(page);
    });

    test("N opens the new-comanda sheet and Esc closes it; Ctrl+K focuses the search; Esc closes the panel; [ folds the menu", async ({
      page,
    }) => {
      await page.goto("/salao/comanda/c7");
      await expect(
        page.getByRole("region", { name: "Comanda aberta" }).getByRole("heading", { name: "Ana" }),
      ).toBeVisible();
      await semAnimacao(page);

      await page.keyboard.press("n");
      const folha = page.getByRole("dialog", { name: "Nova comanda" });
      await expect(folha).toBeVisible();
      await expect(folha.getByRole("textbox", { name: "Número ou nome" })).toBeFocused();
      await expect(folha.getByRole("group", { name: "Teclado numérico" })).toHaveCount(0);
      await semAnimacao(page);
      const caixa = await folha.boundingBox();
      expect(Math.round(caixa?.width ?? 0)).toBe(540);
      expect(Math.round(caixa?.y ?? 0)).toBe(Math.round(900 * 0.13));
      expect(Math.round((caixa?.x ?? 0) + (caixa?.width ?? 0) / 2)).toBe(720);
      await shoot(page, `computador-atalho-n-${tema}`);
      await page.keyboard.press("Escape");
      await expect(folha).toHaveCount(0);
      await expect(page).toHaveURL(/\/salao\/comanda\/c7$/);

      await page.keyboard.press("Control+k");
      await expect(page.getByLabel("Buscar comanda")).toBeFocused();
      await page.keyboard.type("n[");
      await expect(page.getByLabel("Buscar comanda")).toHaveValue("n[");
      await page.getByLabel("Buscar comanda").fill("");
      await page.locator("body").click({ position: { x: 700, y: 5 } });

      await page.keyboard.press("[");
      await expect(page.locator(".app-shell")).toHaveAttribute("data-menu", "trilho");
      expect(await larguraDe(page, ".app-lateral")).toBe(72);
      await shoot(page, `computador-menu-trilho-${tema}`);
      await page.keyboard.press("[");
      await expect(page.locator(".app-shell")).toHaveAttribute("data-menu", "completo");

      await page.keyboard.press("Escape");
      await expect(page).toHaveURL(/\/salao$/);
      await expect(page.getByText("Escolha uma comanda para ver a conta.")).toBeVisible();
    });

    test("types '3 chopp' + Enter in the comanda search and the round holds 3", async ({ page }) => {
      await page.goto("/salao/comanda/c7");
      const campo = page.getByLabel("Buscar produto");
      await campo.fill("3 chopp");
      await campo.press("Enter");

      const painel = page.getByRole("region", { name: "Comanda aberta" });
      await expect(painel.getByRole("group", { name: "Chopp 300ml" })).toBeVisible();
      await expect(painel.getByText("3 itens", { exact: false })).toBeVisible();
      await expect(campo).toHaveValue("");
      await expect(page.getByText("Sem sinal")).toHaveCount(0);
      await semAnimacao(page);
      await shoot(page, `computador-lancamento-rapido-${tema}`);
    });

    test("tabs through the menu first, then the header, the list and the panel, with a visible ring", async ({
      page,
    }) => {
      await page.goto("/salao/comanda/c7");
      await expect(
        page.getByRole("region", { name: "Comanda aberta" }).getByRole("heading", { name: "Ana" }),
      ).toBeVisible();
      await semAnimacao(page);

      const ordem: string[] = [];
      for (let i = 0; i < 23; i += 1) {
        await page.keyboard.press("Tab");
        ordem.push(
          await page.evaluate(() => {
            const el = document.activeElement as HTMLElement | null;
            const faixa = el?.closest(".app-lateral")
              ? "menu"
              : el?.closest(".app-topbar")
                ? "cabecalho"
                : el?.closest(".dividido-painel")
                  ? "painel"
                  : el?.closest(".comandas")
                    ? "lista"
                    : "outro";
            return faixa;
          }),
        );
        if (i === 8) {
          const anel = await page.evaluate(() => {
            const estilo = getComputedStyle(document.activeElement as Element);
            return { estilo: estilo.outlineStyle, largura: Number.parseFloat(estilo.outlineWidth) };
          });
          expect(anel.estilo).not.toBe("none");
          expect(anel.largura).toBeGreaterThanOrEqual(2);
          await shoot(page, `computador-foco-tab-${tema}`);
        }
      }
      const primeira = (faixa: string) => ordem.indexOf(faixa);
      expect(primeira("menu")).toBe(0);
      expect(primeira("menu")).toBeLessThan(primeira("cabecalho"));
      expect(primeira("cabecalho")).toBeLessThan(primeira("lista"));
      expect(primeira("lista")).toBeLessThan(primeira("painel"));
      expect(ordem).not.toContain("outro");
    });
  });
}

test.describe("salão on the computer, the phone has no shortcut (control)", () => {
  test("N does nothing on the phone", async ({ page }) => {
    test.skip(page.viewportSize()?.width !== 390, "390 control only");
    await simularApi(page);
    await page.goto("/salao");
    await expect(page.getByRole("list", { name: "Comandas abertas" })).toBeVisible();

    await page.keyboard.press("n");
    await page.keyboard.press("Control+k");

    await expect(page.getByRole("dialog")).toHaveCount(0);
    await expect(page.getByLabel("Buscar comanda")).not.toBeFocused();
  });
});
