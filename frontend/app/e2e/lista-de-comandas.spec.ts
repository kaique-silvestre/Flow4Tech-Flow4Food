import { mkdirSync } from "node:fs";
import { join } from "node:path";
import AxeBuilder from "@axe-core/playwright";
import { expect, type Page, test } from "@playwright/test";
import { lerSeed, SENHA } from "./seed";

/**
 * F4-0132: the comanda list as the approved prototype draws it (`#m=mobile`): context header, search with an icon, filter chips with their tally, one card per comanda (name, value, time open, waiter,
 * state in words) and a tab bar with icons. Shot at both phone sizes in both themes (the judging is the design reviewer's, not this spec's); axe runs strict over the populated list.
 */
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

async function novaComandaComChopp(page: Page, nome: string) {
  await page.getByRole("button", { name: "Nova comanda" }).click();
  await page.getByRole("dialog", { name: "Nova comanda" }).getByRole("button", { name: "Dar um nome" }).click();
  await page.getByLabel("Nome da comanda").fill(nome);
  await page.getByRole("dialog", { name: "Dar um nome" }).getByRole("button", { name: "OK" }).click();
  await expect(page.getByRole("heading", { level: 2, name: nome })).toBeVisible();
  await page.getByRole("button", { name: "Adicionar Chopp 300ml" }).click();
  await page.getByRole("button", { name: "Enviar", exact: true }).click();
  await page.getByRole("dialog", { name: "Enviar rodada" }).getByRole("button", { name: "Enviar agora" }).click();
  await expect(page.getByText("Rodada enviada", { exact: false })).toBeVisible();
}

const abas = (page: Page) => page.getByRole("navigation", { name: "Seções do salão" });

async function paraALista(page: Page) {
  await abas(page).getByRole("link", { name: "Comandas" }).click();
  await expect(page.getByRole("group", { name: "Filtrar comandas" })).toBeVisible();
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

async function axeEstrito(page: Page, tela: string) {
  await page.addStyleTag({
    content: "*, *::before, *::after { transition: none !important; animation: none !important; }",
  });
  const resultado = await new AxeBuilder({ page }).withTags(["wcag2a", "wcag2aa", "wcag21a", "wcag21aa"]).analyze();
  expect(
    resultado.violations.map((v) => ({ regra: v.id, elementos: v.nodes.map((n) => n.target.join(" ")) })),
    tela,
  ).toEqual([]);
}

test("the list shows the context header, search, chips with tally and one card per comanda, in both themes", async ({
  page,
}) => {
  await entrarComoGarcom2(page);
  const aberta = `Aberta ${Date.now() % 100000}`;
  const aguardando = `Conta ${Date.now() % 100000}`;
  await novaComandaComChopp(page, aberta);
  await paraALista(page);
  await novaComandaComChopp(page, aguardando);
  await page.getByRole("button", { name: "Ver pré-conta" }).click();
  const folha = page.getByRole("dialog", { name: "Pré-conta" });
  await folha.getByRole("button", { name: "Pedir a conta" }).click();
  await expect(folha.getByRole("button", { name: /^Conta pedida/ })).toBeDisabled();
  await folha.getByRole("button", { name: "Voltar" }).click();
  await paraALista(page);

  // Context header: company, unit and person come from the session, with the theme button next to Sair.
  await expect(page.getByText("Bar A · Centro · Garcom de Teste")).toBeVisible();
  await expect(page.getByRole("button", { name: "Mudar para o tema escuro" })).toBeVisible();
  await expect(page.getByRole("button", { name: "Sair" })).toBeVisible();

  // Search with an icon and the prototype's words.
  const busca = page.getByRole("searchbox", { name: "Buscar comanda" });
  await expect(busca).toHaveAttribute("placeholder", "Buscar mesa, balcão ou nome");

  // Chips with their tally; a waiter who sees only their own has no "Todas".
  const filtros = page.getByRole("group", { name: "Filtrar comandas" });
  await expect(filtros.getByRole("button", { name: /^Minhas [1-9]/ })).toHaveAttribute("aria-pressed", "true");
  await expect(filtros.getByRole("button", { name: /^Abertas [1-9]/ })).toBeVisible();
  await expect(filtros.getByRole("button", { name: /^Aguardando [1-9]/ })).toBeVisible();
  await expect(filtros.getByRole("button", { name: /^Todas/ })).toHaveCount(0);

  // Cards: value in reais, time open and who, and the state in words.
  const cartaoAberta = page.getByRole("link", { name: new RegExp(aberta) });
  await expect(cartaoAberta).toContainText(/R\$\s\d/);
  await expect(cartaoAberta).toContainText(/aberta.* · Você/);
  await expect(cartaoAberta).toContainText("Aberta");
  const cartaoConta = page.getByRole("link", { name: new RegExp(aguardando) });
  await expect(cartaoConta).toContainText("Aguardando pagamento");

  await shoot(page, "33-lista-clara");
  await semHorizontal(page);
  expect(await alvosPequenos(page)).toEqual([]);
  await axeEstrito(page, "lista clara");

  // Dark theme through the header button (remembered on the device).
  await page.getByRole("button", { name: "Mudar para o tema escuro" }).click();
  await expect(page.locator("html")).toHaveAttribute("data-theme", "dark");
  await shoot(page, "33-lista-escura");
  await axeEstrito(page, "lista escura");

  // Filters narrow the list and the search keeps working inside them.
  await filtros.getByRole("button", { name: /^Aguardando/ }).click();
  await expect(cartaoConta).toBeVisible();
  await expect(cartaoAberta).toHaveCount(0);
  await shoot(page, "33-lista-filtro-aguardando-escura");
  await filtros.getByRole("button", { name: /^Minhas/ }).click();
  await busca.fill(aberta);
  await expect(cartaoAberta).toBeVisible();
  await expect(cartaoConta).toHaveCount(0);
  await busca.fill("zzzz-nada");
  await expect(page.getByText("Nada com “zzzz-nada”. Tente o número da comanda.")).toBeVisible();
  await shoot(page, "33-lista-busca-vazia-escura");

  // The tab bar has icons with labels and the theme survives a reload.
  await page.reload();
  await expect(page.locator("html")).toHaveAttribute("data-theme", "dark");
  await expect(abas(page).getByRole("link", { name: "Comandas" }).locator("svg")).toBeVisible();
  await page.getByRole("button", { name: "Mudar para o tema claro" }).click();
  await expect(page.locator("html")).toHaveAttribute("data-theme", "light");
});

test("an empty list says so with the single accent action, in both themes", async ({ page }) => {
  await page.goto("/salao/entrar");
  await page.getByLabel("Usuário ou e-mail").fill("garcom");
  await page.getByLabel("Código da organização").fill(seed!.organizacaoA);
  await page.getByLabel("Senha").fill(SENHA);
  await page.getByRole("button", { name: "Entrar" }).click();
  await expect(page.getByRole("heading", { level: 2, name: "Minhas comandas" })).toBeVisible();

  await expect(page.getByText("Nenhuma comanda aberta. Toque em Nova comanda.")).toBeVisible();
  await expect(page.getByText("Bar A · Centro · Garcom da Casa")).toBeVisible();
  await shoot(page, "33-lista-vazia-clara");
  await axeEstrito(page, "lista vazia clara");
  await page.getByRole("button", { name: "Mudar para o tema escuro" }).click();
  await shoot(page, "33-lista-vazia-escura");
  await axeEstrito(page, "lista vazia escura");
});
