import { screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, describe, expect, it, vi } from "vitest";
import { json, renderEm, sessaoJson, stubRede } from "../../test-utils";
import { SENHA_MINIMO, trocarSenhaSchema } from "./TrocarSenhaPage";

afterEach(() => {
  vi.unstubAllGlobals();
});

const temporaria = { "POST /refresh": json(sessaoJson({ deveTrocarSenha: true })) };

describe("temporary password session", () => {
  it("reaches only the password change, from any address", async () => {
    stubRede(temporaria);

    const { router } = await renderEm("/salao/caixa");

    expect(router.state.location.pathname).toBe("/salao/trocar-senha");
    expect(await screen.findByRole("heading", { level: 1, name: "Escolha sua senha" })).toBeVisible();
    expect(screen.queryByRole("navigation", { name: "Seções do salão" })).toBeNull();
  });

  it("shows the policy inline before calling the server (at least 10 characters, equal confirmation)", async () => {
    const chamadas = stubRede(temporaria);
    await renderEm("/salao/trocar-senha");

    await userEvent.type(screen.getByLabelText("Senha provisória"), "provisoria");
    await userEvent.type(screen.getByLabelText("Nova senha"), "curta");
    await userEvent.type(screen.getByLabelText("Repita a nova senha"), "outra");
    await userEvent.click(screen.getByRole("button", { name: "Salvar senha" }));

    expect(await screen.findByText(`Use pelo menos ${SENHA_MINIMO} caracteres.`, { selector: "p[role=alert]" })).toBeVisible();
    expect(screen.getByText("As senhas não são iguais.")).toBeVisible();
    expect(chamadas.some((c) => c.path === "/sessao/trocar-senha")).toBe(false);
  });

  it("maps the server refusals to the right field", async () => {
    stubRede({ ...temporaria, "POST /sessao/trocar-senha": json({ codigo: "senha-atual-invalida", problemas: [] }, { status: 400 }) });
    await renderEm("/salao/trocar-senha");
    await userEvent.type(screen.getByLabelText("Senha provisória"), "provisoria");
    await userEvent.type(screen.getByLabelText("Nova senha"), "uma-senha-nova-longa");
    await userEvent.type(screen.getByLabelText("Repita a nova senha"), "uma-senha-nova-longa");

    await userEvent.click(screen.getByRole("button", { name: "Salvar senha" }));

    expect(await screen.findByText("Senha provisória incorreta.")).toBeVisible();

    stubRede({ ...temporaria, "POST /sessao/trocar-senha": json({ codigo: "senha-vazada", problemas: [] }, { status: 400 }) });
    await userEvent.click(screen.getByRole("button", { name: "Salvar senha" }));
    expect(await screen.findByText("Essa senha apareceu em vazamentos. Escolha outra.")).toBeVisible();

    stubRede({ ...temporaria, "POST /sessao/trocar-senha": json({ codigo: "senha-invalida", problemas: ["IgualASenhaAtual"] }, { status: 400 }) });
    await userEvent.click(screen.getByRole("button", { name: "Salvar senha" }));
    expect(await screen.findByText("A nova senha precisa ser diferente da provisória.")).toBeVisible();

    stubRede({ ...temporaria, "POST /sessao/trocar-senha": json({ codigo: "verificacao-indisponivel", problemas: [] }, { status: 503 }) });
    await userEvent.click(screen.getByRole("button", { name: "Salvar senha" }));
    expect(await screen.findByText("Não foi possível verificar a senha agora. Tente de novo em instantes.")).toBeVisible();
  });

  it("takes the new session after the change and goes on to the next screen the session owes", async () => {
    stubRede({
      ...temporaria,
      "POST /sessao/trocar-senha": json(sessaoJson({ deveTrocarSenha: false, requerSelecaoDeUnidade: true, unidadeAtivaId: null })),
      "GET /unidades": json([{ id: "u1", nome: "Centro" }, { id: "u2", nome: "Praia" }]),
    });
    const { router } = await renderEm("/salao/trocar-senha");
    await userEvent.type(screen.getByLabelText("Senha provisória"), "provisoria");
    await userEvent.type(screen.getByLabelText("Nova senha"), "uma-senha-nova-longa");
    await userEvent.type(screen.getByLabelText("Repita a nova senha"), "uma-senha-nova-longa");

    await userEvent.click(screen.getByRole("button", { name: "Salvar senha" }));

    expect(await screen.findByRole("heading", { level: 1, name: "Escolha a unidade" })).toBeVisible();
    expect(router.state.location.pathname).toBe("/salao/unidade");
  });

  it("signs out from the password change", async () => {
    stubRede({ ...temporaria, "POST /logout": new Response(null, { status: 204 }) });
    const { router } = await renderEm("/salao/trocar-senha");

    await userEvent.click(screen.getByRole("button", { name: "Sair" }));

    await screen.findByRole("heading", { level: 1, name: "Entrar" });
    expect(router.state.location.pathname).toBe("/salao/entrar");
  });
});

describe("password policy", () => {
  const base = { senhaAtual: "provisoria", novaSenha: "uma-senha-nova-longa", confirmacao: "uma-senha-nova-longa" };

  it("accepts a ten-character password and refuses a shorter one or a mismatch", () => {
    expect(trocarSenhaSchema.safeParse({ ...base, novaSenha: "1234567890", confirmacao: "1234567890" }).success).toBe(true);
    expect(trocarSenhaSchema.safeParse({ ...base, novaSenha: "123456789", confirmacao: "123456789" }).success).toBe(false);
    expect(trocarSenhaSchema.safeParse({ ...base, confirmacao: "diferente-diferente" }).success).toBe(false);
  });

  it("asks for no symbols or capitals", () => {
    expect(trocarSenhaSchema.safeParse({ ...base, novaSenha: "apenas minusculas e espacos", confirmacao: "apenas minusculas e espacos" }).success).toBe(true);
  });
});

describe("unit choice", () => {
  const dosUnidades = { "POST /refresh": json(sessaoJson({ requerSelecaoDeUnidade: true, unidadeAtivaId: null })) };

  it("lists the units and lands on the comandas after choosing one", async () => {
    const chamadas = stubRede({
      ...dosUnidades,
      "GET /unidades": json([{ id: "u1", nome: "Centro" }, { id: "u2", nome: "Praia" }]),
      "POST /sessao/unidade-ativa": json({ unidadeAtivaId: "u2" }),
      "GET /comandas": json([]),
    });
    const { router } = await renderEm("/salao");
    expect(router.state.location.pathname).toBe("/salao/unidade");

    await userEvent.click(await screen.findByRole("button", { name: "Praia" }));

    expect(await screen.findByRole("heading", { level: 2, name: "Nenhuma comanda aberta" })).toBeVisible();
    expect(chamadas.find((c) => c.path === "/sessao/unidade-ativa")?.body).toEqual({ unidadeDestinoId: "u2" });
  });

  it("explains that there is no unit and offers to sign out", async () => {
    stubRede({ ...dosUnidades, "GET /unidades": json([]), "POST /logout": new Response(null, { status: 204 }) });
    await renderEm("/salao/unidade");

    expect(await screen.findByRole("heading", { level: 1, name: "Sem unidade" })).toBeVisible();
    expect(screen.getByText(/não está vinculado a nenhuma unidade/)).toBeVisible();
    await userEvent.click(screen.getByRole("button", { name: "Sair" }));

    expect(await screen.findByRole("heading", { level: 1, name: "Entrar" })).toBeVisible();
  });

  it("shows a skeleton while loading and a retry when the load fails", async () => {
    let falhar = true;
    stubRede({ ...dosUnidades, "GET /unidades": () => (falhar ? new Response(null, { status: 500 }) : json([{ id: "u1", nome: "Centro" }])) });
    await renderEm("/salao/unidade");

    expect(await screen.findByText("Não foi possível carregar as unidades.")).toBeVisible();
    falhar = false;
    await userEvent.click(screen.getByRole("button", { name: "Tentar novamente" }));

    expect(await screen.findByRole("button", { name: "Centro" })).toBeVisible();
  });

  it("keeps the choice open and says so when the server refuses the unit", async () => {
    stubRede({ ...dosUnidades, "GET /unidades": json([{ id: "u1", nome: "Centro" }]), "POST /sessao/unidade-ativa": new Response(null, { status: 403 }) });
    await renderEm("/salao/unidade");

    await userEvent.click(await screen.findByRole("button", { name: "Centro" }));

    expect(await screen.findByText("Não foi possível entrar nesta unidade.")).toBeVisible();
    expect(screen.getByRole("heading", { level: 1, name: "Escolha a unidade" })).toBeVisible();
  });
});

describe("the app behind the session", () => {
  it("opens the comanda list for a ready session and signs out from the top bar", async () => {
    stubRede({ "POST /refresh": json(sessaoJson()), "GET /comandas": json([]), "POST /logout": new Response(null, { status: 204 }) });
    const { router } = await renderEm("/salao");
    expect(await screen.findByRole("heading", { level: 2, name: "Nenhuma comanda aberta" })).toBeVisible();
    expect(router.state.location.pathname).toBe("/salao");

    await userEvent.click(screen.getByRole("button", { name: "Sair" }));

    expect(await screen.findByRole("heading", { level: 1, name: "Entrar" })).toBeVisible();
  });

  it("keeps a signed-in person out of the sign-in screen", async () => {
    stubRede({ "POST /refresh": json(sessaoJson()), "GET /comandas": json([]) });

    const { router } = await renderEm("/salao/entrar");

    expect(router.state.location.pathname).toBe("/salao");
  });

  it("shows a retry when the list does not load, and the rows when it does", async () => {
    let falhar = true;
    stubRede({
      "POST /refresh": json(sessaoJson()),
      "GET /comandas": () => (falhar ? new Response(null, { status: 500 }) : json([{ id: "c1", numero: 12, numeroTexto: "#12", identificacao: "Mesa 7" }])),
    });
    await renderEm("/salao");
    expect(await screen.findByText("Não foi possível carregar as comandas.")).toBeVisible();

    falhar = false;
    await userEvent.click(screen.getByRole("button", { name: "Tentar novamente" }));

    expect(await screen.findByText("#12")).toBeVisible();
    expect(screen.getByText("Mesa 7")).toBeVisible();
  });
});
