import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { renderHook, waitFor } from "@testing-library/react";
import type { ReactNode } from "react";
import { afterEach, describe, expect, it, vi } from "vitest";
import { json, sessaoJson, stubRede } from "../../test-utils";
import { useFluxoDeComandas } from "./fluxo";

afterEach(() => vi.unstubAllGlobals());

const envolver = ({ children }: { children: ReactNode }) => (
  <QueryClientProvider client={new QueryClient()}>{children}</QueryClientProvider>
);
const fluxo = (texto: string) => () => new Response(texto, { headers: { "Content-Type": "text/event-stream" } });
const adiar = () => {
  const esperas: number[] = [];
  return { esperas, esperar: (ms: number) => new Promise<void>((r) => setTimeout(() => (esperas.push(ms), r()), 0)) };
};

describe("live stream reconnection", () => {
  it("keeps growing the backoff when the stream opens and drops without a resync", async () => {
    stubRede({ "POST /refresh": json(sessaoJson()), "GET /comandas/fluxo": fluxo(": aberto\n\n") });
    const { esperas, esperar } = adiar();

    const { result, unmount } = renderHook(() => useFluxoDeComandas(true, esperar), { wrapper: envolver });

    await waitFor(() => expect(esperas.length).toBeGreaterThanOrEqual(3));
    expect(esperas.slice(0, 3)).toEqual([1000, 2000, 4000]);
    expect(result.current).toBe(true);
    unmount();
  });

  it("goes back to the first step once a resync shows the stream is up", async () => {
    stubRede({
      "POST /refresh": json(sessaoJson()),
      "GET /comandas/fluxo": fluxo('event: resync\ndata: {"motivo":"abertura","comandas":[]}\n\n'),
    });
    const { esperas, esperar } = adiar();

    const { result, unmount } = renderHook(() => useFluxoDeComandas(true, esperar), { wrapper: envolver });

    await waitFor(() => expect(esperas.length).toBeGreaterThanOrEqual(3));
    expect(esperas.slice(0, 3)).toEqual([1000, 1000, 1000]);
    expect(result.current).toBe(false);
    unmount();
  });

  it("sends Last-Event-ID of the last event it saw when it reconnects", async () => {
    let tentativa = 0;
    const chamadas = stubRede({
      "POST /refresh": json(sessaoJson()),
      "GET /comandas/fluxo": () => {
        tentativa += 1;
        return fluxo(
          tentativa === 1
            ? 'id: e7\nevent: comanda\ndata: {"eventoId":"e7","comandas":[]}\n\n'
            : 'event: resync\ndata: {"motivo":"reconexao","comandas":[]}\n\n',
        )();
      },
    });
    const { esperas, esperar } = adiar();

    const { unmount } = renderHook(() => useFluxoDeComandas(true, esperar), { wrapper: envolver });

    await waitFor(() => expect(esperas.length).toBeGreaterThanOrEqual(2));
    const abertas = chamadas.filter((c) => c.path === "/comandas/fluxo");
    expect(abertas[0]?.headers.get("Last-Event-ID")).toBeNull();
    expect(abertas[1]?.headers.get("Last-Event-ID")).toBe("e7");
    unmount();
  });

  it("renews the session once on a 401 and repeats the request, without calling it a failure", async () => {
    let tentativa = 0;
    const chamadas = stubRede({
      "POST /refresh": json(sessaoJson()),
      "GET /comandas/fluxo": () => {
        tentativa += 1;
        return tentativa === 1
          ? new Response("", { status: 401 })
          : fluxo('event: resync\ndata: {"motivo":"abertura","comandas":[]}\n\n')();
      },
    });
    const { esperas, esperar } = adiar();

    const { result, unmount } = renderHook(() => useFluxoDeComandas(true, esperar), { wrapper: envolver });

    await waitFor(() => expect(esperas.length).toBeGreaterThanOrEqual(1));
    expect(chamadas.filter((c) => c.path === "/refresh")).toHaveLength(1);
    expect(chamadas.filter((c) => c.path === "/comandas/fluxo").length).toBeGreaterThanOrEqual(2);
    expect(esperas[0]).toBe(1000);
    expect(result.current).toBe(false);
    unmount();
  });
});
