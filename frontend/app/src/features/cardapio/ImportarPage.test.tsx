import { screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, describe, expect, it, vi } from "vitest";
import { json, renderEm, sessaoJson, stubRede, TODAS_AS_PERMISSOES } from "../../test-utils";

afterEach(() => {
  vi.unstubAllGlobals();
});

const PODE = [...TODAS_AS_PERMISSOES, "produto:gerenciar", "cardapio:importar"];
const resumo = (extra: Record<string, number> = {}) => ({
  categoriasCriadas: 0,
  categoriasAtualizadas: 0,
  produtosCriados: 0,
  produtosAtualizados: 0,
  gruposCriados: 0,
  gruposAtualizados: 0,
  opcoesCriadas: 0,
  opcoesAtualizadas: 0,
  ...extra,
});
const modelo = {
  colunas: ["tipo", "codigo"],
  tipos: ["produto"],
  formatos: ["csv", "xlsx"],
  exemploCsv: "tipo;codigo\nproduto;X1",
  linhasMaximas: 2000,
  bytesMaximos: 1048576,
};
const valida = { valida: true, erros: [], itens: [], resumo: resumo({ produtosCriados: 2, categoriasCriadas: 1 }) };
const comErros = {
  valida: false,
  erros: [
    { linha: 4, coluna: "preco", mensagem: "Preço inválido." },
    { linha: 4, coluna: "nome", mensagem: "Nome em branco." },
    { linha: 9, coluna: "codigo", mensagem: "Código repetido." },
  ],
  itens: [],
  resumo: resumo(),
};
const aplicada = (extra: Record<string, unknown> = {}) => ({
  importacaoId: "x",
  jaAplicada: false,
  resumo: resumo({ produtosCriados: 2, categoriasCriadas: 1 }),
  ...extra,
});

const base = (extra: Record<string, ReturnType<typeof json>> = {}) => ({
  "POST /refresh": json(sessaoJson()),
  "GET /comandas": json([]),
  "GET /sessao/permissoes": json({ permissoes: PODE }),
  "GET /cardapio/importacao/modelo": json(modelo),
  ...extra,
});

async function escolher(nome = "cardapio.csv") {
  const entrada = await screen.findByLabelText("Arquivo da planilha");
  await userEvent.upload(entrada, new File(["tipo;codigo"], nome, { type: "text/csv" }));
}

describe("menu import screen", () => {
  it("is absent for who may not import (forbidden, without saying why)", async () => {
    stubRede(base({ "GET /sessao/permissoes": json({ permissoes: TODAS_AS_PERMISSOES }) }));
    await renderEm("/salao/cardapio/importar");
    expect(await screen.findByText("Você não tem permissão para isso.")).toBeInTheDocument();
  });

  it("treats a 403 on the template as forbidden", async () => {
    stubRede(base({ "GET /cardapio/importacao/modelo": new Response(null, { status: 403 }) }));
    await renderEm("/salao/cardapio/importar");
    expect(await screen.findByText("Você não tem permissão para isso.")).toBeInTheDocument();
  });

  it("shows an error with retry when the template fails to load", async () => {
    stubRede(base({ "GET /cardapio/importacao/modelo": new Response(null, { status: 500 }) }));
    await renderEm("/salao/cardapio/importar");
    expect(await screen.findByText("Não foi possível carregar a importação.")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Tentar novamente" })).toBeInTheDocument();
  });

  it("downloads the template as a csv", async () => {
    stubRede(base());
    const criar = vi.fn(() => "blob:x");
    URL.createObjectURL = criar;
    URL.revokeObjectURL = vi.fn();
    const clique = vi.spyOn(HTMLAnchorElement.prototype, "click").mockImplementation(() => undefined);
    await renderEm("/salao/cardapio/importar");
    await userEvent.click(await screen.findByRole("button", { name: "Baixar modelo" }));
    expect(criar).toHaveBeenCalled();
    expect(clique).toHaveBeenCalled();
    clique.mockRestore();
  });

  it("reads errors by file line and offers no confirm when the preview is invalid", async () => {
    stubRede(base({ "POST /cardapio/importacao/previa": json(comErros) }));
    await renderEm("/salao/cardapio/importar");
    await escolher();
    expect(await screen.findByText("3 erros para corrigir")).toBeInTheDocument();
    expect(screen.getByText("Linha 4")).toBeInTheDocument();
    expect(screen.getByText("Linha 9")).toBeInTheDocument();
    expect(screen.getByText("Nome em branco.")).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Confirmar importação" })).toBeNull();
  });

  it("previews counts, applies with one importacaoId, and links to the menu", async () => {
    const chamadas = stubRede(
      base({
        "POST /cardapio/importacao/previa": json(valida),
        "POST /cardapio/importacao": json(aplicada(), { status: 201 }),
      }),
    );
    await renderEm("/salao/cardapio/importar");
    await escolher();
    expect(await screen.findByText("Pronta")).toBeInTheDocument();
    expect(screen.getByText("2 novos · 0 atualizados")).toBeInTheDocument();
    await userEvent.click(screen.getByRole("button", { name: "Confirmar importação" }));
    expect(await screen.findByText("Cardápio importado")).toBeInTheDocument();
    expect(screen.getByRole("link", { name: "Ver o cardápio" })).toHaveAttribute("href", "/salao/cardapio");
    const envio = chamadas.find((c) => c.path === "/cardapio/importacao");
    expect(envio?.body).toMatchObject({ formato: "csv", conteudoBase64: btoa("tipo;codigo") });
    expect(String((envio?.body as Record<string, unknown> | undefined)?.["importacaoId"])).toMatch(/^[0-9a-f-]{36}$/);
  });

  it("says 'nothing to change' for a repeated import", async () => {
    stubRede(
      base({
        "POST /cardapio/importacao/previa": json(valida),
        "POST /cardapio/importacao": json(aplicada({ jaAplicada: true })),
      }),
    );
    await renderEm("/salao/cardapio/importar");
    await escolher();
    await userEvent.click(await screen.findByRole("button", { name: "Confirmar importação" }));
    expect(await screen.findByText("Nada para mudar")).toBeInTheDocument();
  });

  it("on a 403 at apply (price limit) shows the bare sentence and goes back to choosing", async () => {
    stubRede(
      base({
        "POST /cardapio/importacao/previa": json(valida),
        "POST /cardapio/importacao": new Response(null, { status: 403 }),
      }),
    );
    await renderEm("/salao/cardapio/importar");
    await escolher();
    await userEvent.click(await screen.findByRole("button", { name: "Confirmar importação" }));
    expect(await screen.findByText("Você não tem permissão para isso.")).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Confirmar importação" })).toBeNull();
  });

  it("on 409 with a reused id asks for the file again", async () => {
    stubRede(
      base({
        "POST /cardapio/importacao/previa": json(valida),
        "POST /cardapio/importacao": json({ codigo: "importacao-id-reutilizado" }, { status: 409 }),
      }),
    );
    await renderEm("/salao/cardapio/importar");
    await escolher();
    await userEvent.click(await screen.findByRole("button", { name: "Confirmar importação" }));
    expect(await screen.findByText(/Escolha o arquivo de novo/)).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Escolher arquivo" })).toBeInTheDocument();
  });

  it("on 422 at apply shows the error report", async () => {
    stubRede(
      base({
        "POST /cardapio/importacao/previa": json(valida),
        "POST /cardapio/importacao": json(comErros, { status: 422 }),
      }),
    );
    await renderEm("/salao/cardapio/importar");
    await escolher();
    await userEvent.click(await screen.findByRole("button", { name: "Confirmar importação" }));
    expect(await screen.findByText("3 erros para corrigir")).toBeInTheDocument();
  });

  it("shows the server sentence for an invalid file", async () => {
    stubRede(base({ "POST /cardapio/importacao/previa": json({ codigo: "arquivo-com-formula" }, { status: 400 }) }));
    await renderEm("/salao/cardapio/importar");
    await escolher();
    expect(await screen.findByText(/tem fórmula/)).toBeInTheDocument();
  });

  it("refuses a wrong type without calling the server", async () => {
    const chamadas = stubRede(base());
    await renderEm("/salao/cardapio/importar");
    const entrada = await screen.findByLabelText("Arquivo da planilha");
    await userEvent.upload(entrada, new File(["x"], "a.pdf"), { applyAccept: false });
    expect(await screen.findByText("Use um arquivo CSV ou XLSX.")).toBeInTheDocument();
    expect(chamadas.some((c) => c.path.includes("previa"))).toBe(false);
  });

  it("offline at apply keeps the file and a resend carries the same importacaoId", async () => {
    let tentativa = 0;
    const chamadas = stubRede({
      ...base({ "POST /cardapio/importacao/previa": json(valida) }),
      "POST /cardapio/importacao": () => {
        if (tentativa++ === 0) {
          throw new TypeError("offline");
        }

        return json(aplicada(), { status: 201 });
      },
    });
    await renderEm("/salao/cardapio/importar");
    await escolher();
    await userEvent.click(await screen.findByRole("button", { name: "Confirmar importação" }));
    expect(await screen.findByText(/Sem conexão/)).toBeInTheDocument();
    await userEvent.click(screen.getByRole("button", { name: "Tentar de novo" }));
    expect(await screen.findByText("Cardápio importado")).toBeInTheDocument();
    const ids = chamadas
      .filter((c) => c.path === "/cardapio/importacao")
      .map((c) => (c.body as { importacaoId: string }).importacaoId);
    expect(ids).toHaveLength(2);
    expect(ids[0]).toBe(ids[1]);
  });

  it("offline at the preview offers a retry", async () => {
    let tentativa = 0;
    stubRede({
      ...base(),
      "POST /cardapio/importacao/previa": () => {
        if (tentativa++ === 0) {
          throw new TypeError("offline");
        }

        return json(valida);
      },
    });
    await renderEm("/salao/cardapio/importar");
    await escolher();
    expect(await screen.findByText(/Sem conexão/)).toBeInTheDocument();
    await userEvent.click(screen.getByRole("button", { name: "Tentar de novo" }));
    expect(await screen.findByText("Pronta")).toBeInTheDocument();
  });
});
