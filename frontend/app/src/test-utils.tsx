import type { paths } from "@flow4tech/api-client";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { createMemoryHistory, RouterProvider } from "@tanstack/react-router";
import { render } from "@testing-library/react";
import { vi } from "vitest";
import { reiniciarFilaParaTestes } from "./features/fila/fila";
import { reiniciarSessaoParaTestes } from "./features/sessao/sessao";
import { createAppRouter } from "./router";

export interface Chamada {
  method: string;
  path: string;
  headers: Headers;
  body: unknown;
}

type Resposta = Response | ((chamada: Chamada) => Response | Promise<Response>);

export function json(corpo: unknown, init?: ResponseInit): Response {
  return Response.json(corpo, init);
}

type Metodo = "get" | "post" | "put" | "delete" | "patch";

/** The JSON body the contract (`openapi.json` via the generated `schema.ts`) declares for `METHOD path` answering `status`; `never` if the contract has no such answer. */
type CorpoDoContrato<P extends keyof paths, M extends Metodo, S extends number> = paths[P] extends infer Rota
  ? M extends keyof Rota
    ? Rota[M] extends { responses: infer Respostas }
      ? S extends keyof Respostas
        ? Respostas[S] extends { content: { "application/json": infer Corpo } }
          ? Corpo
          : never
        : never
      : never
    : never
  : never;

/**
 * A stub answer typed by the API contract: the body must be exactly what the route's declared `status` answer carries,
 * so renaming or retyping a field in the API fails `pnpm -r typecheck` here instead of passing a test that the real API would not satisfy.
 * `rota` is the contract's template (`"/comandas/{comandaId}/pagamentos"`), not the concrete path the stub matches on.
 * Use plain `json()` only for what the contract does not type (empty bodies, a network failure, a deliberately malformed answer).
 */
export function jsonDoContrato<P extends keyof paths, M extends Metodo, S extends number>(
  _metodo: M,
  _rota: P,
  status: S,
  corpo: CorpoDoContrato<P, M, S>,
): Response {
  return Response.json(corpo, { status });
}

/** A token shaped like the server's (header.payload.signature) whose `sub` is the account; the client only reads `sub` to keep one person's queue from being sent by another. */
export function tokenDe(conta: string): string {
  const parte = (o: unknown) => btoa(JSON.stringify(o)).replace(/=+$/, "").replace(/\+/g, "-").replace(/\//g, "_");
  return `${parte({ alg: "HS256", typ: "JWT" })}.${parte({ sub: conta })}.assinatura`;
}

export function sessaoJson(
  extra: Partial<{
    deveTrocarSenha: boolean;
    requerSelecaoDeUnidade: boolean;
    unidadeAtivaId: string | null;
    accessToken: string;
  }> = {},
) {
  return {
    accessToken: tokenDe("conta-1"),
    expiraEmSegundos: 300,
    sessaoId: "0197e2e0-0000-7000-8000-000000000001",
    unidadeAtivaId: "0197e2e0-0000-7000-8000-0000000000a1",
    deveTrocarSenha: false,
    requerSelecaoDeUnidade: false,
    sessaoExpiraEm: "2026-12-01T00:00:00Z",
    ...extra,
  };
}

export const TODAS_AS_PERMISSOES = ["caixa:abrir", "caixa:fechar", "caixa:ver", "comanda:abrir", "comanda:ver"];

/** Stubs the network edge by `METHOD /path`. Anything not listed fails the test loudly (no silent default). Returns the recorded calls. */
export function stubRede(informadas: Record<string, Resposta>) {
  // The caller's permissions are incidental to most tests: everyone may do everything unless a test says otherwise.
  const rotas: Record<string, Resposta> = {
    "GET /sessao/permissoes": json({ permissoes: TODAS_AS_PERMISSOES }),
    ...informadas,
  };
  const chamadas: Chamada[] = [];
  const fetchFalso = vi.fn(async (entrada: Request) => {
    const url = new URL(entrada.url);
    const texto = entrada.method === "GET" ? "" : await entrada.clone().text();
    const chamada: Chamada = {
      method: entrada.method,
      path: url.pathname,
      headers: entrada.headers,
      body: texto ? JSON.parse(texto) : undefined,
    };
    chamadas.push(chamada);
    const chave = `${entrada.method} ${url.pathname}`;
    // A key may name a path parameter as ":id" (a round id the test cannot know in advance).
    const resposta =
      rotas[chave] ??
      Object.entries(rotas).find(
        ([padrao]) => padrao.includes(":") && new RegExp(`^${padrao.replace(/:[a-z]+/gi, "[^/]+")}$`).test(chave),
      )?.[1];
    if (!resposta) {
      throw new Error(`rota sem stub: ${entrada.method} ${url.pathname}`);
    }

    return typeof resposta === "function" ? resposta(chamada) : resposta.clone();
  });
  vi.stubGlobal("fetch", fetchFalso);
  return chamadas;
}

export async function renderEm(caminho: string) {
  reiniciarSessaoParaTestes();
  reiniciarFilaParaTestes();
  const router = createAppRouter(createMemoryHistory({ initialEntries: [caminho] }));
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  const utilitarios = render(
    <QueryClientProvider client={queryClient}>
      <RouterProvider router={router} />
    </QueryClientProvider>,
  );
  await router.load();
  return { router, queryClient, ...utilitarios };
}
