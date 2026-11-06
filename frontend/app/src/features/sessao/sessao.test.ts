import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { json, sessaoJson, stubRede } from "../../test-utils";
import { api, destinoDaSessao, entrar, lerSessao, renovar, reiniciarSessaoParaTestes, restaurar, sair } from "./sessao";

beforeEach(() => {
  reiniciarSessaoParaTestes();
});

afterEach(() => {
  vi.unstubAllGlobals();
});

describe("session in memory", () => {
  it("signs in with the typed organization and the CSRF header, and keeps the token only in memory", async () => {
    const chamadas = stubRede({ "POST /login": json(sessaoJson()) });

    const r = await entrar({ organizacaoDoLogin: "org-1", usuario: "garcom", senha: "x" });

    expect(r).toEqual({ tipo: "ok" });
    expect(chamadas[0]?.headers.get("X-F4T-Csrf")).toBe("1");
    expect(chamadas[0]?.body).toEqual({ organizacaoDoLogin: "org-1", usuario: "garcom", email: null, senha: "x" });
    expect(lerSessao()).toMatchObject({ status: "ativa", accessToken: "token-de-teste", deveTrocarSenha: false });
    expect(JSON.stringify(window.localStorage)).not.toContain("token-de-teste");
    expect(JSON.stringify(window.sessionStorage)).not.toContain("token-de-teste");
  });

  it("signs in by e-mail without an organization", async () => {
    const chamadas = stubRede({ "POST /login": json(sessaoJson()) });

    await entrar({ email: "dono@bar.test", senha: "x" });

    expect(chamadas[0]?.body).toEqual({ organizacaoDoLogin: null, usuario: null, email: "dono@bar.test", senha: "x" });
  });

  it.each([
    [401, "invalido"],
    [400, "invalido"],
    [428, "captcha"],
    [500, "erro"],
  ])("maps a %i answer to %s and does not start a session", async (status, tipo) => {
    stubRede({ "POST /login": new Response(null, { status }) });

    const r = await entrar({ email: "a@b.test", senha: "x" });

    expect(r.tipo).toBe(tipo);
    expect(lerSessao().status).not.toBe("ativa");
  });

  it("reads the wait of a blocked login from Retry-After and falls back to 30 seconds", async () => {
    stubRede({ "POST /login": new Response(null, { status: 429, headers: { "Retry-After": "75" } }) });
    expect(await entrar({ email: "a@b.test", senha: "x" })).toEqual({ tipo: "bloqueado", esperaSegundos: 75 });

    stubRede({ "POST /login": new Response(null, { status: 429 }) });
    expect(await entrar({ email: "a@b.test", senha: "x" })).toEqual({ tipo: "bloqueado", esperaSegundos: 30 });
  });

  it("reports a network failure as an error, never as a wrong password", async () => {
    vi.stubGlobal("fetch", async () => {
      throw new TypeError("network");
    });

    expect(await entrar({ email: "a@b.test", senha: "x" })).toEqual({ tipo: "erro" });
  });
});

describe("single refresh", () => {
  it("sends ONE refresh for any number of simultaneous callers", async () => {
    let resolver: (r: Response) => void = () => undefined;
    const chamadas = stubRede({ "POST /refresh": () => new Promise<Response>((r) => { resolver = r; }) as unknown as Response });

    const todas = Promise.all([renovar(), renovar(), renovar()]);
    await vi.waitFor(() => expect(chamadas).toHaveLength(1));
    resolver(json(sessaoJson()));

    expect(await todas).toEqual([true, true, true]);
    expect(chamadas.filter((c) => c.path === "/refresh")).toHaveLength(1);
  });

  it("restores a session from the cookie on load, once", async () => {
    const chamadas = stubRede({ "POST /refresh": json(sessaoJson()) });

    await Promise.all([restaurar(), restaurar()]);

    expect(lerSessao().status).toBe("ativa");
    expect(chamadas).toHaveLength(1);
  });

  it("becomes anonymous when there is no session to restore", async () => {
    stubRede({ "POST /refresh": new Response(null, { status: 401 }) });

    await restaurar();

    expect(lerSessao().status).toBe("anonima");
  });

  it("renews once on a 401 and repeats the request with the new token", async () => {
    stubRede({ "POST /login": json(sessaoJson({ accessToken: "velho" })) });
    await entrar({ email: "a@b.test", senha: "x" });
    let tentativas = 0;
    const chamadas = stubRede({
      "GET /unidades": (c) => {
        tentativas += 1;
        return c.headers.get("Authorization") === "Bearer novo" ? json([{ id: "u1", nome: "Centro" }]) : new Response(null, { status: 401 });
      },
      "POST /refresh": json(sessaoJson({ accessToken: "novo" })),
    });

    const { data } = await api.GET("/unidades");

    expect(data).toEqual([{ id: "u1", nome: "Centro" }]);
    expect(tentativas).toBe(2);
    expect(chamadas.map((c) => `${c.method} ${c.path}`)).toEqual(["GET /unidades", "POST /refresh", "GET /unidades"]);
  });

  it("ends the session when the refresh is refused", async () => {
    stubRede({ "POST /login": json(sessaoJson()) });
    await entrar({ email: "a@b.test", senha: "x" });
    stubRede({ "GET /unidades": new Response(null, { status: 401 }), "POST /refresh": new Response(null, { status: 401 }) });

    const { response } = await api.GET("/unidades");

    expect(response.status).toBe(401);
    expect(lerSessao().status).toBe("anonima");
  });
});

describe("sign out and destination", () => {
  it("forgets the session even when the server is unreachable", async () => {
    stubRede({ "POST /login": json(sessaoJson()) });
    await entrar({ email: "a@b.test", senha: "x" });
    vi.stubGlobal("fetch", async () => {
      throw new TypeError("network");
    });

    await sair();

    expect(lerSessao().status).toBe("anonima");
  });

  it("sends the person to the screen the session still owes", async () => {
    expect(destinoDaSessao({ status: "anonima" })).toBe("/salao/entrar");
    stubRede({ "POST /login": json(sessaoJson({ deveTrocarSenha: true, requerSelecaoDeUnidade: true })) });
    await entrar({ email: "a@b.test", senha: "x" });
    expect(destinoDaSessao(lerSessao())).toBe("/salao/trocar-senha");

    stubRede({ "POST /login": json(sessaoJson({ requerSelecaoDeUnidade: true, unidadeAtivaId: null })) });
    await entrar({ email: "a@b.test", senha: "x" });
    expect(destinoDaSessao(lerSessao())).toBe("/salao/unidade");

    stubRede({ "POST /login": json(sessaoJson()) });
    await entrar({ email: "a@b.test", senha: "x" });
    expect(destinoDaSessao(lerSessao())).toBe("/salao");
  });
});
