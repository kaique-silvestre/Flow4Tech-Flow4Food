import { mkdirSync } from "node:fs";
import { join } from "node:path";
import AxeBuilder from "@axe-core/playwright";
import { expect, type Page, test } from "@playwright/test";
import { semAnimacao, simularApi } from "./api-simulada";

/**
 * F4-0144: the agenda de funcionamento at /gestao/agenda on the computer (1440x900), light and dark. The API is answered inside the browser (no backend);
 * the answers of `/agenda` below stand in for the server only to the extent the screen needs (it refuses a window that closes past the 05:00 rollover, like the real one).
 * Visual reference: .scratch/S-00-novo-sistema/prototipos/index.html#m=web&screen=configuracoes&ctab=op.
 */
test.use({ serviceWorkers: "block" });

const shots = join(import.meta.dirname, "..", "..", "..", ".scratch", "S-05-gestao-web-e-telas", "shots", "F4-0144");

async function shoot(page: Page, name: string) {
  mkdirSync(shots, { recursive: true });
  await page.screenshot({ path: join(shots, `${name}-1440x900.png`) });
}

async function axe(page: Page) {
  const resultado = await new AxeBuilder({ page }).withTags(["wcag2a", "wcag2aa", "wcag21a", "wcag21aa"]).analyze();
  expect(resultado.violations.map((v) => `${v.id}: ${v.nodes.map((n) => n.target.join(" ")).join(" | ")}`)).toEqual([]);
}

type Janela = { abre: string; fecha: string } | null;
const noite = { abre: "18:00", fecha: "23:00" };

/** The server's side of the screen: the agenda in memory, PUT and POST change it, a window that closes after the 05:00 rollover is a 400 `invalido`. */
async function simularAgenda(page: Page) {
  const estado = {
    diaOperacional: "2026-11-17",
    fuso: "America/Sao_Paulo",
    virada: "05:00",
    semana: [
      null,
      noite,
      noite,
      noite,
      noite,
      { abre: "17:00", fecha: "03:00" },
      { abre: "15:00", fecha: "03:00" },
    ] as Janela[],
    excecoes: [{ dia: "2026-12-25", janela: null as Janela }],
    estendidoAte: null as string | null,
    janelaDeHoje: noite as Janela,
    abertoAgora: true,
  };
  const passaDaVirada = (j: Janela) => j !== null && j.fecha < j.abre && j.fecha > "05:00";
  await page.route(
    (url) => url.pathname === "/sessao/permissoes",
    (route) => route.fulfill({ json: { permissoes: ["comanda:ver", "organizacao:configurar"] } }),
  );
  await page.route(
    (url) => url.pathname.startsWith("/agenda"),
    async (route) => {
      const { pathname } = new URL(route.request().url());
      const metodo = route.request().method();
      if (metodo === "GET") {
        return route.fulfill({ json: estado });
      }

      const corpo = route.request().postDataJSON() as {
        semana?: Janela[];
        excecoes?: { dia: string; janela: Janela }[];
        fecha?: string;
      };
      if (pathname === "/agenda/semana" && corpo.semana) {
        if (corpo.semana.some(passaDaVirada)) {
          return route.fulfill({ status: 400, json: { codigo: "invalido" } });
        }

        estado.semana = corpo.semana;
        estado.janelaDeHoje = corpo.semana[1] ?? null;
      } else if (pathname === "/agenda/excecoes" && corpo.excecoes) {
        estado.excecoes = corpo.excecoes;
      } else if (pathname === "/agenda/estender-hoje" && corpo.fecha) {
        estado.estendidoAte = corpo.fecha;
        estado.janelaDeHoje = { abre: "18:00", fecha: corpo.fecha };
      }

      return route.fulfill({ status: 204, body: "" });
    },
  );
}

for (const tema of ["light", "dark"] as const) {
  test.describe(`agenda de funcionamento, ${tema}`, () => {
    test.beforeEach(async ({ page }) => {
      test.skip(page.viewportSize()?.width !== 1440, "computer project only");
      await page.emulateMedia({ colorScheme: tema });
      await simularApi(page);
      await simularAgenda(page);
    });

    test("shows the agenda, saves the week, refuses a window past the rollover, axe strict", async ({ page }) => {
      await page.goto("/gestao/agenda");

      await expect(page.getByRole("heading", { name: "Agenda de funcionamento" })).toBeVisible();
      await expect(page.getByRole("link", { name: "Agenda" })).toHaveAttribute("aria-current", "page");
      await semAnimacao(page);
      await shoot(page, `agenda-${tema}`);
      await axe(page);

      await page.getByRole("switch", { name: "Segunda: fechado" }).click();
      await page.getByLabel("Segunda: abre").fill("12:00");
      await page.getByLabel("Segunda: fecha").fill("22:00");
      await page.getByRole("button", { name: "Salvar semana" }).click();
      await expect(page.getByText("Semana salva.")).toBeVisible();
      await shoot(page, `semana-salva-${tema}`);

      await page.getByLabel("Terça: fecha").fill("06:00");
      await page.getByRole("button", { name: "Salvar semana" }).click();
      await expect(page.getByText(/Horário não aceito\. Abra às 05:00 ou depois/)).toBeVisible();
      await shoot(page, `erro-janela-${tema}`);
      await axe(page);
    });

    test("adds an exception and extends today", async ({ page }) => {
      await page.goto("/gestao/agenda");
      await page.getByRole("heading", { name: "Exceções por data" }).waitFor();
      await semAnimacao(page);

      await page.getByLabel("Data", { exact: true }).fill("2026-11-20");
      await page.getByRole("switch", { name: "Fechado nesse dia" }).click();
      await page.getByLabel("Abre às").fill("14:00");
      await page.getByLabel("Fecha às").fill("03:00");
      await page.getByLabel("Motivo (opcional)").fill("Jogo no telão");
      await page.getByRole("button", { name: "Adicionar exceção" }).click();
      await expect(page.getByText("Exceção salva.")).toBeVisible();
      await expect(page.getByRole("list", { name: "Exceções cadastradas" }).getByRole("listitem")).toHaveCount(2);
      await shoot(page, `excecao-${tema}`);
      await axe(page);

      await page.getByRole("button", { name: "Estender hoje" }).click();
      await page.getByLabel("Novo fechamento").fill("01:00");
      await shoot(page, `estender-formulario-${tema}`);
      await page.getByRole("button", { name: "Estender", exact: true }).click();
      await expect(page.getByText("Hoje estendido.")).toBeVisible();
      await expect(page.getByText(/Estendido até 01:00/)).toBeVisible();
      await shoot(page, `estendido-${tema}`);
      await axe(page);
    });
  });
}
