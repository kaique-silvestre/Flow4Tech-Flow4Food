import { act, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { json, renderEm, sessaoJson, stubRede } from "../../test-utils";
import { ehEmail, entrarSchema, organizacaoValida } from "./schema";

const ORG = "0197e2e0-0000-7000-8000-00000000000a";
const semSessao = { "POST /refresh": new Response(null, { status: 401 }) };

beforeEach(() => {
  window.localStorage.clear();
});

afterEach(() => {
  vi.unstubAllGlobals();
  vi.useRealTimers();
});

async function preencher(identificador: string, senha: string, organizacao?: string) {
  await userEvent.type(screen.getByLabelText("Usuário ou e-mail"), identificador);
  if (organizacao !== undefined) {
    await userEvent.type(await screen.findByLabelText("Código da organização"), organizacao);
  }

  await userEvent.type(screen.getByLabelText("Senha"), senha);
}

describe("sign-in screen", () => {
  it("is where an anonymous person lands, from any address", async () => {
    stubRede(semSessao);

    const { router } = await renderEm("/salao");

    expect(router.state.location.pathname).toBe("/salao/entrar");
    expect(await screen.findByRole("heading", { level: 1, name: "Entrar" })).toBeVisible();
    expect(screen.getByRole("button", { name: "Entrar" })).toBeVisible();
  });

  it("asks for the organization only for an operational user, and remembers it on the device", async () => {
    stubRede({ ...semSessao, "POST /login": json(sessaoJson()), "GET /comandas": json([]) });
    await renderEm("/salao/entrar");
    expect(screen.queryByLabelText("Código da organização")).toBeNull();

    await userEvent.type(screen.getByLabelText("Usuário ou e-mail"), "dono@bar.test");
    expect(screen.queryByLabelText("Código da organização")).toBeNull();
    await userEvent.clear(screen.getByLabelText("Usuário ou e-mail"));
    await preencher("garcom", "uma-senha-longa", ORG);
    await userEvent.click(screen.getByRole("button", { name: "Entrar" }));

    expect(await screen.findByText("Nenhuma comanda aberta. Toque em Nova comanda.")).toBeVisible();
    expect(window.localStorage.getItem("f4.organizacao")).toBe(ORG);
  });

  it("prefills the saved organization next time", async () => {
    window.localStorage.setItem("f4.organizacao", ORG);
    stubRede(semSessao);
    await renderEm("/salao/entrar");

    await userEvent.type(screen.getByLabelText("Usuário ou e-mail"), "garcom");

    expect(await screen.findByLabelText("Código da organização")).toHaveValue(ORG);
  });

  it("signs in by e-mail without the organization", async () => {
    const chamadas = stubRede({ ...semSessao, "POST /login": json(sessaoJson()), "GET /comandas": json([]) });
    await renderEm("/salao/entrar");

    await preencher("dono@bar.test", "uma-senha-longa");
    await userEvent.click(screen.getByRole("button", { name: "Entrar" }));

    await screen.findByText("Nenhuma comanda aberta. Toque em Nova comanda.");
    expect(chamadas.find((c) => c.path === "/login")?.body).toMatchObject({ email: "dono@bar.test", organizacaoDoLogin: null });
  });

  it("shows ONE generic failure and clears the password, whatever was wrong", async () => {
    stubRede({ ...semSessao, "POST /login": new Response(null, { status: 401 }) });
    await renderEm("/salao/entrar");

    await preencher("garcom", "errada-errada", ORG);
    await userEvent.click(screen.getByRole("button", { name: "Entrar" }));

    expect(await screen.findByRole("alert")).toHaveTextContent("Usuário ou senha inválidos.");
    expect(screen.getByLabelText("Senha")).toHaveValue("");
    expect(screen.getByLabelText("Usuário ou e-mail")).toHaveValue("garcom");
  });

  it("answers a malformed organization code exactly like a wrong password, without calling the server", async () => {
    const chamadas = stubRede(semSessao);
    await renderEm("/salao/entrar");

    await preencher("garcom", "uma-senha-longa", "codigo-qualquer");
    await userEvent.click(screen.getByRole("button", { name: "Entrar" }));

    expect(await screen.findByRole("alert")).toHaveTextContent("Usuário ou senha inválidos.");
    expect(chamadas.some((c) => c.path === "/login")).toBe(false);
  });

  it("validates the empty fields inline, without calling the server", async () => {
    const chamadas = stubRede(semSessao);
    await renderEm("/salao/entrar");

    await userEvent.click(screen.getByRole("button", { name: "Entrar" }));

    expect(await screen.findByText("Informe o usuário ou e-mail.")).toBeVisible();
    expect(screen.getByText("Informe a senha.")).toBeVisible();
    expect(chamadas.some((c) => c.path === "/login")).toBe(false);
  });

  it("blocks the form with the wait from Retry-After and counts it down", async () => {
    stubRede({ ...semSessao, "POST /login": new Response(null, { status: 429, headers: { "Retry-After": "90" } }) });
    await renderEm("/salao/entrar");
    await preencher("garcom", "uma-senha-longa", ORG);

    vi.useFakeTimers({ shouldAdvanceTime: true });
    await userEvent.click(screen.getByRole("button", { name: "Entrar" }));

    expect(await screen.findByRole("status")).toHaveTextContent("Tente de novo em 1:30.");
    expect(screen.getByRole("button", { name: "Entrar" })).toBeDisabled();
    expect(screen.getByLabelText("Usuário ou e-mail")).toBeDisabled();
    // One second per tick: the next timer is armed by the render that follows each tick.
    await act(async () => {
      await vi.advanceTimersByTimeAsync(1000);
    });
    await act(async () => {
      await vi.advanceTimersByTimeAsync(1000);
    });
    expect(screen.getByRole("status")).toHaveTextContent("Tente de novo em 1:28.");
  });

  it("shows the verification-required state on a 428", async () => {
    stubRede({ ...semSessao, "POST /login": new Response(null, { status: 428 }) });
    await renderEm("/salao/entrar");
    await preencher("garcom", "uma-senha-longa", ORG);

    await userEvent.click(screen.getByRole("button", { name: "Entrar" }));

    expect(await screen.findByText("Verificação necessária")).toBeVisible();
    expect(screen.getByRole("button", { name: "Entrar" })).toBeEnabled();
  });

  it("says the connection failed, not that the password is wrong", async () => {
    stubRede({ ...semSessao, "POST /login": () => Promise.reject(new TypeError("network")) as unknown as Response });
    await renderEm("/salao/entrar");
    await preencher("garcom", "uma-senha-longa", ORG);

    await userEvent.click(screen.getByRole("button", { name: "Entrar" }));

    expect(await screen.findByRole("alert")).toHaveTextContent("Sem conexão ou servidor fora do ar");
  });

  it("shows the spinner in the button and blocks a second send while the request is in flight", async () => {
    let responder: (r: Response) => void = () => undefined;
    const chamadas = stubRede({ ...semSessao, "POST /login": () => new Promise<Response>((r) => { responder = r; }) as unknown as Response, "GET /comandas": json([]) });
    await renderEm("/salao/entrar");
    await preencher("dono@bar.test", "uma-senha-longa");

    await userEvent.click(screen.getByRole("button", { name: "Entrar" }));

    await waitFor(() => expect(screen.getByRole("button", { name: "Entrar" })).toHaveAttribute("aria-busy", "true"));
    await userEvent.click(screen.getByRole("button", { name: "Entrar" }));
    expect(chamadas.filter((c) => c.path === "/login")).toHaveLength(1);
    responder(json(sessaoJson()));
    await screen.findByText("Nenhuma comanda aberta. Toque em Nova comanda.");
  });

  it("shows and hides the password on demand", async () => {
    stubRede(semSessao);
    await renderEm("/salao/entrar");

    expect(screen.getByLabelText("Senha")).toHaveAttribute("type", "password");
    await userEvent.click(screen.getByRole("button", { name: "Mostrar" }));

    expect(screen.getByLabelText("Senha")).toHaveAttribute("type", "text");
    expect(screen.getByRole("button", { name: "Ocultar" })).toHaveAttribute("aria-pressed", "true");
  });
});

describe("sign-in rules", () => {
  it("tells an e-mail from an operational user by the at-sign", () => {
    expect(ehEmail("a@b.test")).toBe(true);
    expect(ehEmail("garcom")).toBe(false);
  });

  it("needs the organization only for an operational user", () => {
    expect(entrarSchema.safeParse({ identificador: "garcom", senha: "x", organizacao: "" }).success).toBe(false);
    expect(entrarSchema.safeParse({ identificador: "garcom", senha: "x", organizacao: ORG }).success).toBe(true);
    expect(entrarSchema.safeParse({ identificador: "a@b.test", senha: "x", organizacao: "" }).success).toBe(true);
  });

  it("accepts only an id-shaped organization code", () => {
    expect(organizacaoValida(ORG)).toBe(true);
    expect(organizacaoValida(` ${ORG} `)).toBe(true);
    expect(organizacaoValida("bar-do-ze")).toBe(false);
  });
});
