import { useQueryClient } from "@tanstack/react-query";
import { useEffect, useState } from "react";
import { api } from "../sessao/sessao";
import type { Comanda } from "./tipos";

export interface VersaoDaComanda {
  comandaId: string;
  versao: number;
}

export interface EventoLido {
  nome: string;
  id: string | null;
  dados: { comandas?: VersaoDaComanda[] };
}

/**
 * Splits what arrived on the stream into whole SSE events. `resto` is the unfinished tail: give it back with the next chunk.
 * Comments (`: batimento`) and data that is not JSON carry nothing and are dropped.
 */
export function lerEventos(resto: string, pedaco: string): { eventos: EventoLido[]; resto: string } {
  const partes = (resto + pedaco).replaceAll("\r\n", "\n").split("\n\n");
  const novoResto = partes.pop() ?? "";
  const eventos: EventoLido[] = [];
  for (const bloco of partes) {
    let nome = "message";
    let id: string | null = null;
    const dados: string[] = [];
    for (const linha of bloco.split("\n")) {
      if (linha.startsWith("event:")) {
        nome = linha.slice(6).trim();
      } else if (linha.startsWith("id:")) {
        id = linha.slice(3).trim();
      } else if (linha.startsWith("data:")) {
        dados.push(linha.slice(5).trim());
      }
    }

    if (dados.length === 0) {
      continue;
    }

    try {
      eventos.push({ nome, id, dados: JSON.parse(dados.join("\n")) as EventoLido["dados"] });
    } catch {
      // A broken event is lost like any other; the next resync (on reconnect) recovers by version.
    }
  }

  return { eventos, resto: novoResto };
}

/** The version is the order: a comanda only needs reloading when the stream says it is ahead of what the screen holds (or the screen does not hold it). */
export function precisaRecarregar(comandas: VersaoDaComanda[], naTela: ReadonlyMap<string, number>): boolean {
  return comandas.some((c) => c.versao > (naTela.get(c.comandaId) ?? Number.NEGATIVE_INFINITY));
}

/** Wait before the n-th consecutive attempt (1-based): 1 s, doubling, at most 15 s. */
export function esperaDoBackoff(tentativa: number): number {
  return Math.min(1000 * 2 ** (Math.max(tentativa, 1) - 1), 15_000);
}

/** A stream that stayed up this long counts as healthy even if it ended. */
const ESTAVEL_MS = 5000;

const dormir = (ms: number, sinal: AbortSignal) =>
  new Promise<void>((resolver) => {
    const timer = setTimeout(resolver, ms);
    sinal.addEventListener(
      "abort",
      () => {
        clearTimeout(timer);
        resolver();
      },
      { once: true },
    );
  });

/**
 * Keeps the comandas list live while `ativo` (the list is on screen): one SSE stream (`GET /comandas/fluxo`) read with the session's fetch, because the stream needs the Authorization header that
 * EventSource cannot send. A `comanda` event reloads the list only when a version is ahead of the screen; `resync` (the server sends it on every connection) always reloads. A dropped stream is
 * reopened with backoff and `Last-Event-ID`; closed when the screen leaves. Returns whether the stream is currently down (after a failure), for the "reconectando" notice.
 */
export function useFluxoDeComandas(
  ativo: boolean,
  esperar: (ms: number, sinal: AbortSignal) => Promise<void> = dormir,
) {
  const queryClient = useQueryClient();
  const [reconectando, setReconectando] = useState(false);

  useEffect(() => {
    if (!ativo) {
      return;
    }

    const controle = new AbortController();
    const { signal } = controle;
    const recarregar = () => void queryClient.invalidateQueries({ queryKey: ["comandas"] });
    const naTela = () =>
      new Map((queryClient.getQueryData<Comanda[]>(["comandas"]) ?? []).map((c) => [c.id, c.versao] as const));

    void (async () => {
      let falhas = 0;
      let ultimoId: string | null = null;
      while (!signal.aborted) {
        try {
          const { response } = await api.GET("/comandas/fluxo", {
            parseAs: "stream",
            signal,
            headers: ultimoId ? { "Last-Event-ID": ultimoId } : {},
          });
          if (!response.ok || !response.body) {
            throw new Error("fluxo");
          }

          const leitor = response.body.pipeThrough(new TextDecoderStream()).getReader();
          let resto = "";
          const abriuEm = Date.now();
          let firme = false;
          for (;;) {
            const { done, value } = await leitor.read();
            if (done) {
              break;
            }

            const lido = lerEventos(resto, value);
            resto = lido.resto;
            for (const evento of lido.eventos) {
              if (evento.nome === "resync") {
                // The server sends it on every connection it accepts: only now is the stream really up.
                firme = true;
                falhas = 0;
                setReconectando(false);
              }

              ultimoId = evento.id ?? ultimoId;
              if (evento.nome === "resync" || precisaRecarregar(evento.dados.comandas ?? [], naTela())) {
                recarregar();
              }
            }
          }

          if (Date.now() - abriuEm >= ESTAVEL_MS) {
            falhas = 0;
          } else if (!firme) {
            // Opened and dropped at once without the resync: not a live stream, so the backoff keeps growing instead of spinning at one step.
            throw new Error("fluxo-instavel");
          }
        } catch {
          if (signal.aborted) {
            return;
          }

          falhas += 1;
          setReconectando(true);
        }

        // Even a stream that ended well (the server recycles it every few minutes) waits the first backoff step: never a tight loop.
        await esperar(esperaDoBackoff(falhas), signal);
      }
    })();

    return () => {
      controle.abort();
      setReconectando(false);
    };
  }, [ativo, queryClient, esperar]);

  return reconectando;
}
