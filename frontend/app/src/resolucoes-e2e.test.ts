import { readFileSync } from "node:fs";
import { resolve } from "node:path";
import { describe, expect, it } from "vitest";
import config from "../playwright.config";

/** F4-0138: the four resolutions of ADR-019 stay in the Playwright config and in the axe scan; dropping one fails here, not silently in CI. */
const projetos = config.projects ?? [];
const quatro = [
  ["phone-390x844", 390, 844],
  ["phone-360x740", 360, 740],
  ["tablet-820x1180", 820, 1180],
  ["computer-1440x900", 1440, 900],
] as const;
const comoRegex = (valor: unknown, campo: string, nome: string): RegExp | undefined => {
  if (valor === undefined) return undefined;
  if (valor instanceof RegExp) return valor;
  throw new Error(`${nome}: ${campo} must be a single RegExp for this guard to read it`);
};
const rodaOCompartilhado = (nome: string) => {
  const p = projetos.find((x) => x.name === nome);
  if (!p) return false;
  const arquivo = "salao-quatro-resolucoes.spec.ts";
  const match = comoRegex(p.testMatch, "testMatch", nome);
  const ignore = comoRegex(p.testIgnore, "testIgnore", nome);
  return (match === undefined || match.test(arquivo)) && (ignore === undefined || !ignore.test(arquivo));
};

describe("Playwright projects for the four resolutions", () => {
  it.each(quatro)("%s has the viewport %ix%i", (nome, largura, altura) => {
    expect(projetos.find((p) => p.name === nome)?.use?.viewport).toEqual({ width: largura, height: altura });
  });

  it.each(quatro)("%s runs the shared flows and the axe scan (salao-quatro-resolucoes.spec.ts)", (nome) => {
    expect(rodaOCompartilhado(nome)).toBe(true);
  });

  it("the shared spec scans axe strict in both themes", () => {
    const fonte = readFileSync(resolve(__dirname, "../e2e/salao-quatro-resolucoes.spec.ts"), "utf8");
    expect(fonte).toContain('["light", "dark"]');
    expect(fonte).toContain("wcag21aa");
    expect(fonte).not.toContain("AXE_REPORT_ONLY");
  });
});
