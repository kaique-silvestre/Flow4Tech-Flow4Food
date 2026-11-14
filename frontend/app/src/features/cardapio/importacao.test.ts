import { describe, expect, it } from "vitest";
import { agruparPorLinha, formatoDoNome, lerArquivo, mensagemDoCodigo, semMudanca } from "./importacao";

const zero = {
  categoriasCriadas: 0,
  categoriasAtualizadas: 0,
  produtosCriados: 0,
  produtosAtualizados: 0,
  gruposCriados: 0,
  gruposAtualizados: 0,
  opcoesCriadas: 0,
  opcoesAtualizadas: 0,
};

describe("menu import logic", () => {
  it("groups errors by file line, in line order", () => {
    const g = agruparPorLinha([
      { linha: 7, coluna: "preco", mensagem: "Preço inválido." },
      { linha: 3, coluna: "nome", mensagem: "Nome em branco." },
      { linha: 7, coluna: "codigo", mensagem: "Código repetido." },
    ]);
    expect(g.map((x) => [x.linha, x.erros.length])).toEqual([
      [3, 1],
      [7, 2],
    ]);
  });

  it("knows when nothing would change", () => {
    expect(semMudanca(zero)).toBe(true);
    expect(semMudanca({ ...zero, produtosCriados: 1 })).toBe(false);
  });

  it("accepts only csv and xlsx by name, and words unknown codes generically", () => {
    expect(formatoDoNome("Cardapio.XLSX")).toBe("xlsx");
    expect(formatoDoNome("a.csv")).toBe("csv");
    expect(formatoDoNome("a.pdf")).toBeNull();
    expect(mensagemDoCodigo("arquivo-com-formula")).toMatch(/fórmula/);
    expect(mensagemDoCodigo("qualquer")).toMatch(/Não foi possível/);
  });

  it("refuses a file over the limit before reading, and mints one id per read file", async () => {
    expect(await lerArquivo(new File(["x".repeat(20)], "a.csv"), 10)).toMatch(/grande demais/);
    expect(await lerArquivo(new File(["x"], "a.txt"), 10)).toMatch(/CSV ou XLSX/);
    const a = await lerArquivo(new File(["a;b"], "a.csv"), 100);
    const b = await lerArquivo(new File(["a;b"], "a.csv"), 100);
    if (typeof a === "string" || typeof b === "string") {
      throw new Error("expected files");
    }

    expect(a.conteudoBase64).toBe(btoa("a;b"));
    expect(a.importacaoId).not.toBe(b.importacaoId);
  });
});
