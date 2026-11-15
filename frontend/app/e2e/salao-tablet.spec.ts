import { mkdirSync } from "node:fs";
import { join } from "node:path";
import AxeBuilder from "@axe-core/playwright";
import { expect, type Page, test } from "@playwright/test";

/**
 * F4-0136: on the tablet (820x1180) the list of comandas and the open comanda sit side by side; on the phone (390x844) there is one column.
 * The API is answered inside the browser (`page.route`), so this runs with no backend. Visual reference: .scratch/S-00-novo-sistema/prototipos/index.html#m=web&open=3.
 */
test.use({ serviceWorkers: "block" });

const shots = join(import.meta.dirname, "..", "e2e-results", "screenshots");
mkdirSync(shots, { recursive: true });

const base64url = (o: unknown) => Buffer.from(JSON.stringify(o)).toString("base64url");
const token = `${base64url({ alg: "HS256", typ: "JWT" })}.${base64url({ sub: "conta-1" })}.assinatura`;
const comanda = (n: number, nome: string, consumoCentavos: number) => ({
  id: `c${n}`,
  unidadeId: "u1",
  numero: n,
  numeroTexto: `#${n}`,
  identificacao: nome,
  mesaId: null,
  canal: "salao",
  garcomId: "conta-1",
  pessoas: null,
  estado: "aberta",
  taxaServicoPercentual: 10,
  taxaServicoRemovida: false,
  versao: 2,
  abertaEm: new Date(Date.now() - 20 * 60_000).toISOString(),
  garcomNome: "Marina",
  consumoCentavos,
  ultimaAtividadeEm: new Date(Date.now() - 2 * 60_000).toISOString(),
});
const item = {
  id: "i1",
  rodadaId: "r1",
  posicao: 1,
  produtoId: "p1",
  produtoNome: "Chopp 300ml",
  precoCentavos: 1250,
  quantidade: 2,
  observacao: null,
  estado: "lancado",
  motivoCancelamento: null,
  canceladoPor: null,
  canceladoEm: null,
};
const respostas: Record<string, unknown> = {
  "POST /refresh": {
    accessToken: token,
    expiraEmSegundos: 300,
    sessaoId: "0197e2e0-0000-7000-8000-000000000001",
    unidadeAtivaId: "0197e2e0-0000-7000-8000-0000000000a1",
    deveTrocarSenha: false,
    requerSelecaoDeUnidade: false,
    sessaoExpiraEm: "2099-12-01T00:00:00Z",
  },
  "GET /sessao/permissoes": {
    permissoes: [
      "comanda:ver",
      "comanda:ver-comandas-de-outros",
      "comanda:abrir",
      "comanda:lancar-item",
      "comanda:cancelar-item",
      "comanda:desfazer-rodada",
      "produto:ver",
      "caixa:ver",
    ],
  },
  "GET /sessao/contexto": { organizacao: "Bar do Zé", unidade: "Unidade Centro", usuario: "Marina" },
  "GET /comandas": [comanda(7, "Ana", 2500), comanda(8, "Bia", 8900), comanda(9, "Balcão 1", 4200)],
  "GET /mesas": [],
  "GET /comandas/c7/conta": {
    consumoCentavos: 2500,
    taxaServicoCentavos: 250,
    totalCentavos: 2750,
    pagoCentavos: 0,
    saldoCentavos: 2750,
    taxaServicoRemovida: false,
    taxaServicoPercentual: 10,
    versao: 2,
    estado: "aberta",
  },
  "GET /comandas/c7/itens": { itens: [item], consumoCentavos: 2500 },
  "GET /cardapio/categorias": [{ id: "cat1", nome: "Bebidas", ativa: true, versao: 1 }],
  "GET /cardapio/produtos": [
    {
      id: "p1",
      categoriaId: "cat1",
      codigo: "CHOPP",
      nome: "Chopp 300ml",
      precoCentavos: 1250,
      moeda: "BRL",
      ativo: true,
      versao: 1,
    },
  ],
  "GET /cardapio/produtos/frequentes": [],
  "GET /cardapio/produtos/favoritos": [],
};
const rotasDaApi = /^\/(login|refresh|logout|sessao|caixa|cardapio|comandas|mesas|configuracoes|pagamentos)(\/|$)/;

async function simularApi(page: Page) {
  await page.route(
    (url) => rotasDaApi.test(url.pathname),
    (route) => {
      const url = new URL(route.request().url());
      const corpo = respostas[`${route.request().method()} ${url.pathname}`];
      return corpo === undefined ? route.fulfill({ status: 404, body: "" }) : route.fulfill({ json: corpo });
    },
  );
}

async function shoot(page: Page, name: string) {
  const size = page.viewportSize();
  await page.screenshot({ path: join(shots, `${name}-${size?.width ?? 0}x${size?.height ?? 0}.png`) });
}

async function semAnimacao(page: Page) {
  await page.addStyleTag({
    content: "*, *::before, *::after { transition: none !important; animation: none !important; }",
  });
}

async function larguraDoElemento(page: Page, nome: string) {
  return (await page.getByRole("region", { name: nome }).boundingBox())?.width ?? 0;
}

const tablet = (page: Page) => page.viewportSize()?.width === 820;

for (const tema of ["light", "dark"] as const) {
  test.describe(`salão on the tablet, ${tema}`, () => {
    test.beforeEach(async ({ page }) => {
      test.skip(!tablet(page), "tablet project only");
      await page.emulateMedia({ colorScheme: tema });
      await simularApi(page);
    });

    test("shows the list and the open comanda side by side, the panel 420 px wide, opened straight by the URL", async ({
      page,
    }) => {
      await page.goto("/salao/comanda/c7");

      const lista = page.getByRole("list", { name: "Comandas abertas" });
      const painel = page.getByRole("region", { name: "Comanda aberta" });
      await expect(lista).toBeVisible();
      await expect(painel.getByRole("heading", { level: 2, name: "Ana" })).toBeVisible();
      const [caixaLista, caixaPainel] = await Promise.all([lista.boundingBox(), painel.boundingBox()]);
      expect(caixaLista && caixaPainel && caixaLista.x + caixaLista.width <= caixaPainel.x).toBe(true);
      expect(Math.round(await larguraDoElemento(page, "Comanda aberta"))).toBe(420);
      expect(
        await page.evaluate(() => document.documentElement.scrollWidth <= document.documentElement.clientWidth),
      ).toBe(true);
      const cabecalho = await page.getByRole("banner").boundingBox();
      expect(cabecalho && caixaPainel && caixaPainel.y >= cabecalho.y + cabecalho.height).toBe(true);
      const barra = await painel.locator(".comanda-barra").boundingBox();
      const preConta = await painel.getByRole("button", { name: "Ver pré-conta" }).boundingBox();
      expect(barra && preConta && preConta.y + preConta.height <= barra.y + 1).toBe(true);
      await semAnimacao(page);
      await shoot(page, `tablet-comanda-${tema}`);
      const resultado = await new AxeBuilder({ page }).withTags(["wcag2a", "wcag2aa", "wcag21a", "wcag21aa"]).analyze();
      expect(resultado.violations.map((v) => `${v.id}: ${v.nodes.map((n) => n.target.join(" ")).join(" | ")}`)).toEqual(
        [],
      );
    });

    test("opens another comanda in the panel and shows the empty panel on /salao", async ({ page }) => {
      await page.goto("/salao");

      const painel = page.getByRole("region", { name: "Comanda aberta" });
      await expect(painel.getByText("Escolha uma comanda para ver a conta.")).toBeVisible();
      const campo = page.getByLabel("Buscar comanda");
      await expect(campo).toHaveAttribute("placeholder", "Buscar comanda");
      expect(await campo.evaluate((el) => el.scrollWidth <= el.clientWidth)).toBe(true);
      const chips = page.getByRole("group", { name: "Filtrar comandas" });
      await expect(chips.getByRole("button")).toHaveCount(4);
      expect(await chips.evaluate((el) => el.scrollWidth <= el.clientWidth)).toBe(true);
      for (const chip of await chips.getByRole("button").all()) {
        const caixa = await chip.boundingBox();
        const grupo = await chips.boundingBox();
        expect(caixa && grupo && caixa.x >= grupo.x && caixa.x + caixa.width <= grupo.x + grupo.width).toBe(true);
      }
      await semAnimacao(page);
      await shoot(page, `tablet-lista-${tema}`);
      const resultado = await new AxeBuilder({ page }).withTags(["wcag2a", "wcag2aa", "wcag21a", "wcag21aa"]).analyze();
      expect(resultado.violations.map((v) => `${v.id}: ${v.nodes.map((n) => n.target.join(" ")).join(" | ")}`)).toEqual(
        [],
      );

      await page.getByRole("list", { name: "Comandas abertas" }).getByRole("link", { name: /Ana/ }).click();
      await expect(page).toHaveURL(/\/salao\/comanda\/c7$/);
      await expect(painel.getByRole("heading", { level: 2, name: "Ana" })).toBeVisible();
    });

    test("shows the keyboard focus ring on a card and on a button of the panel, not hidden by the current-comanda border", async ({
      page,
    }) => {
      await page.goto("/salao/comanda/c7");
      const painel = page.getByRole("region", { name: "Comanda aberta" });
      await expect(painel.getByRole("heading", { level: 2, name: "Ana" })).toBeVisible();
      await semAnimacao(page);

      const cartao = page.getByRole("list", { name: "Comandas abertas" }).getByRole("link", { name: /Ana/ });
      await cartao.focus();
      await page.keyboard.press("Shift+Tab");
      await page.keyboard.press("Tab");
      await expect(cartao).toBeFocused();
      expect(await cartao.evaluate((el) => getComputedStyle(el).outlineWidth)).toBe("3px");
      await shoot(page, `tablet-foco-cartao-${tema}`);

      const botao = painel.getByRole("button", { name: "Repetir última rodada" });
      await botao.focus();
      await page.keyboard.press("Shift+Tab");
      await page.keyboard.press("Tab");
      await expect(botao).toBeFocused();
      expect(await botao.evaluate((el) => getComputedStyle(el).outlineWidth)).toBe("3px");
      await shoot(page, `tablet-foco-painel-${tema}`);
    });

    test("turns the new-comanda sheet into a 420 px side panel on the right", async ({ page }) => {
      await page.goto("/salao/comanda/c7");
      await page.getByRole("button", { name: "Nova comanda" }).click();

      const folha = page.getByRole("dialog", { name: "Nova comanda" });
      await expect(folha).toBeVisible();
      await semAnimacao(page);
      const caixa = await folha.boundingBox();
      expect(Math.round(caixa?.width ?? 0)).toBe(420);
      expect(Math.round((caixa?.x ?? 0) + (caixa?.width ?? 0))).toBe(820);
      await shoot(page, `tablet-folha-${tema}`);
    });
  });
}

test.describe("salão on the tablet keeps the list state", () => {
  test("keeps what was typed in the search when a comanda opens in the panel", async ({ page }) => {
    test.skip(!tablet(page), "tablet project only");
    await simularApi(page);
    await page.goto("/salao");

    await page.getByLabel("Buscar comanda").fill("Ana");
    await page.getByRole("list", { name: "Comandas abertas" }).getByRole("link", { name: /Ana/ }).click();

    await expect(
      page.getByRole("region", { name: "Comanda aberta" }).getByRole("heading", { level: 2, name: "Ana" }),
    ).toBeVisible();
    await expect(page.getByLabel("Buscar comanda")).toHaveValue("Ana");
  });
});

test.describe("salão on the phone stays one column (control)", () => {
  test("shows the list alone, then the comanda alone", async ({ page }) => {
    test.skip(page.viewportSize()?.width !== 390, "390 control only");
    await simularApi(page);

    await page.goto("/salao");
    await expect(page.getByRole("list", { name: "Comandas abertas" })).toBeVisible();
    await expect(page.getByRole("region", { name: "Comanda aberta" })).toHaveCount(0);
    await shoot(page, "celular-lista");

    await page.goto("/salao/comanda/c7");
    await expect(page.getByRole("heading", { level: 2, name: "Ana" })).toBeVisible();
    await expect(page.getByRole("list", { name: "Comandas abertas" })).toHaveCount(0);
    const preConta = await page.getByRole("button", { name: "Ver pré-conta" }).boundingBox();
    const ordem = await page.evaluate(() => {
      const botoes = [...document.querySelectorAll<HTMLElement>("main button")];
      const posicao = (rotulo: string) => botoes.findIndex((b) => b.textContent?.includes(rotulo));
      const y = (rotulo: string) => botoes[posicao(rotulo)]?.getBoundingClientRect().y ?? 0;
      return {
        domPreContaAntes: posicao("Ver pré-conta") < posicao("Repetir última rodada"),
        visualPreContaAntes: y("Ver pré-conta") < y("Repetir última rodada"),
      };
    });
    expect(ordem).toEqual({ domPreContaAntes: true, visualPreContaAntes: true });
    const abas = await page.getByRole("navigation", { name: "Seções do salão" }).boundingBox();
    expect(preConta && abas && preConta.y >= 0 && preConta.y + preConta.height <= abas.y).toBe(true);
    await shoot(page, "celular-comanda");
  });
});
