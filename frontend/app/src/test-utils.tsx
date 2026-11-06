import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { createMemoryHistory, RouterProvider } from "@tanstack/react-router";
import { render } from "@testing-library/react";
import { vi } from "vitest";
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

export function sessaoJson(extra: Partial<{ deveTrocarSenha: boolean; requerSelecaoDeUnidade: boolean; unidadeAtivaId: string | null; accessToken: string }> = {}) {
  return {
    accessToken: "token-de-teste",
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
  const rotas: Record<string, Resposta> = { "GET /sessao/permissoes": json({ permissoes: TODAS_AS_PERMISSOES }), ...informadas };
  const chamadas: Chamada[] = [];
  const fetchFalso = vi.fn(async (entrada: Request) => {
    const url = new URL(entrada.url);
    const texto = entrada.method === "GET" ? "" : await entrada.clone().text();
    const chamada: Chamada = { method: entrada.method, path: url.pathname, headers: entrada.headers, body: texto ? JSON.parse(texto) : undefined };
    chamadas.push(chamada);
    const resposta = rotas[`${entrada.method} ${url.pathname}`];
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
  const router = createAppRouter(createMemoryHistory({ initialEntries: [caminho] }));
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  const utilitarios = render(
    <QueryClientProvider client={queryClient}>
      <RouterProvider router={router} />
    </QueryClientProvider>,
  );
  await router.load();
  return { router, ...utilitarios };
}
