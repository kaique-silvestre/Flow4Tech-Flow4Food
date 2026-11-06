import type { components } from "@flow4tech/api-client";
import { createApiClient } from "@flow4tech/api-client";
import { useSyncExternalStore } from "react";

type SessaoResponse = components["schemas"]["SessaoResponse"];

export type EstadoDaSessao =
  | { status: "desconhecida" }
  | { status: "anonima" }
  | {
      status: "ativa";
      accessToken: string;
      /** When the access token expires (ms since epoch); it is renewed before. */
      expiraEm: number;
      deveTrocarSenha: boolean;
      requerSelecaoDeUnidade: boolean;
      unidadeAtivaId: string | null;
    };

const CSRF = { "X-F4T-Csrf": "1" } as const;

let estado: EstadoDaSessao = { status: "desconhecida" };
const ouvintes = new Set<() => void>();
let renovando: Promise<boolean> | null = null;
let restaurando: Promise<void> | null = null;

function definir(novo: EstadoDaSessao) {
  estado = novo;
  for (const ouvinte of ouvintes) {
    ouvinte();
  }
}

function deResposta(r: SessaoResponse): EstadoDaSessao {
  return {
    status: "ativa",
    accessToken: r.accessToken,
    expiraEm: Date.now() + Number(r.expiraEmSegundos) * 1000,
    deveTrocarSenha: r.deveTrocarSenha,
    requerSelecaoDeUnidade: r.requerSelecaoDeUnidade,
    unidadeAtivaId: r.unidadeAtivaId ?? null,
  };
}

/**
 * The fetch the generated client uses. Adds the bearer token, the CSRF header the cookie routes need, and on a 401 renews the session ONCE (through the single `renovar`) and repeats the request.
 * The access token lives only in memory; the refresh token never reaches JavaScript (HttpOnly cookie).
 */
async function fetchDaSessao(request: Request): Promise<Response> {
  const caminho = new URL(request.url).pathname;
  const publica = caminho === "/login" || caminho === "/refresh" || caminho === "/logout";
  const repetivel = publica ? null : request.clone();
  const enviada = comCabecalhos(request);
  const resposta = await fetch(enviada);
  if (resposta.status !== 401 || publica || repetivel === null) {
    return resposta;
  }

  if (!(await renovar())) {
    return resposta;
  }

  return fetch(comCabecalhos(repetivel));
}

function comCabecalhos(request: Request): Request {
  const headers = new Headers(request.headers);
  if (request.method !== "GET" && request.method !== "HEAD") {
    headers.set("X-F4T-Csrf", CSRF["X-F4T-Csrf"]);
  }

  if (estado.status === "ativa") {
    headers.set("Authorization", `Bearer ${estado.accessToken}`);
  }

  return new Request(request, { headers });
}

/** Same origin as the page: the API paths are proxied (dev, preview) or routed (production) there, so the browser never leaves its origin (cookies, CSRF guard). */
export const api = createApiClient({ baseUrl: window.location.origin, fetch: fetchDaSessao });

/**
 * Renews the session through the refresh cookie. Every caller shares ONE request in flight (two simultaneous refreshes with the same cookie would be read by the server as a reuse and end the
 * session), so this is the single place the session is refreshed from.
 */
export function renovar(): Promise<boolean> {
  renovando ??= (async () => {
    try {
      const { data, response } = await api.POST("/refresh", { headers: CSRF });
      if (data) {
        definir(deResposta(data));
        return true;
      }

      if (response.status === 401 || response.status === 403) {
        definir({ status: "anonima" });
      }

      return false;
    } catch {
      return false;
    } finally {
      renovando = null;
    }
  })();
  return renovando;
}

/** On the first load (or a reload) the cookie may still hold a session: ask once. Resolves when the state is known. */
export function restaurar(): Promise<void> {
  restaurando ??= (async () => {
    if (estado.status !== "desconhecida") {
      return;
    }

    await renovar();
    if (estado.status === "desconhecida") {
      definir({ status: "anonima" });
    }
  })();
  return restaurando;
}

export type ResultadoDeEntrada =
  | { tipo: "ok" }
  | { tipo: "invalido" }
  | { tipo: "bloqueado"; esperaSegundos: number }
  | { tipo: "captcha" }
  | { tipo: "erro" };

export type Credenciais = { senha: string } & ({ email: string } | { organizacaoDoLogin: string; usuario: string });

export async function entrar(credenciais: Credenciais): Promise<ResultadoDeEntrada> {
  try {
    const body = {
      organizacaoDoLogin: "organizacaoDoLogin" in credenciais ? credenciais.organizacaoDoLogin : null,
      usuario: "usuario" in credenciais ? credenciais.usuario : null,
      email: "email" in credenciais ? credenciais.email : null,
      senha: credenciais.senha,
    };
    const { data, response } = await api.POST("/login", { body, headers: CSRF });
    if (data) {
      definir(deResposta(data));
      return { tipo: "ok" };
    }

    if (response.status === 401) {
      return { tipo: "invalido" };
    }

    if (response.status === 429) {
      const espera = Number.parseInt(response.headers.get("Retry-After") ?? "", 10);
      return { tipo: "bloqueado", esperaSegundos: Number.isFinite(espera) && espera > 0 ? espera : 30 };
    }

    if (response.status === 428) {
      return { tipo: "captcha" };
    }

    return { tipo: response.status === 400 ? "invalido" : "erro" };
  } catch {
    return { tipo: "erro" };
  }
}

export async function sair(): Promise<void> {
  try {
    await api.POST("/logout", { headers: CSRF });
  } catch {
    // The server may be unreachable: the device forgets the session anyway.
  }

  definir({ status: "anonima" });
}

/** The session returned by a password change replaces the current one. */
export function aceitarSessao(r: SessaoResponse) {
  definir(deResposta(r));
}

export function definirUnidadeAtiva(unidadeId: string) {
  if (estado.status === "ativa") {
    definir({ ...estado, unidadeAtivaId: unidadeId, requerSelecaoDeUnidade: false });
  }
}

export function lerSessao(): EstadoDaSessao {
  return estado;
}

export function useSessao(): EstadoDaSessao {
  return useSyncExternalStore(
    (ouvinte) => {
      ouvintes.add(ouvinte);
      return () => ouvintes.delete(ouvinte);
    },
    () => estado,
  );
}

/** Where the person must be: the screen that matches the session, and nothing else is reachable until it is done. */
export function destinoDaSessao(e: EstadoDaSessao): "/salao/entrar" | "/salao/trocar-senha" | "/salao/unidade" | "/salao" {
  if (e.status !== "ativa") {
    return "/salao/entrar";
  }

  if (e.deveTrocarSenha) {
    return "/salao/trocar-senha";
  }

  return e.requerSelecaoDeUnidade ? "/salao/unidade" : "/salao";
}

/** Test seam: forget everything (a fresh device). */
export function reiniciarSessaoParaTestes() {
  estado = { status: "desconhecida" };
  renovando = null;
  restaurando = null;
  for (const ouvinte of ouvintes) {
    ouvinte();
  }
}
