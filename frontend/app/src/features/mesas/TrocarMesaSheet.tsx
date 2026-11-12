import { BottomSheet, Button, StatusBadge } from "@flow4tech/design-system";
import { useQueryClient } from "@tanstack/react-query";
import { useEffect, useState } from "react";
import type { Comanda } from "../comandas/tipos";
import { trocarMesa } from "./consultas";
import { type MesaDoMapa, nomeDaMesa } from "./tipos";

interface Props {
  /** The comanda to move, as the list last saw it (its `versao` is what the server compares). Null closes the sheet. */
  comanda: Comanda | null;
  mesas: readonly MesaDoMapa[];
  onClose: () => void;
  /** The comanda is now at `mesa`. */
  onMovida: (comanda: Comanda, mesa: MesaDoMapa) => void;
}

/**
 * "Trocar de mesa": the other tables of the unit, one tap selects, the accent button moves. It needs a connection (not queued). On a conflict the data is refreshed and the chosen
 * table stays selected, so the person only taps Mover again; nothing they did is lost.
 */
export function TrocarMesaSheet({ comanda, mesas, onClose, onMovida }: Props) {
  const queryClient = useQueryClient();
  const [escolhida, setEscolhida] = useState<string | null>(null);
  const [enviando, setEnviando] = useState(false);
  const [erro, setErro] = useState<string | undefined>(undefined);
  const comandaId = comanda?.id ?? null;

  useEffect(() => {
    if (comandaId !== null) {
      setEscolhida(null);
      setErro(undefined);
      setEnviando(false);
    }
  }, [comandaId]);

  const outras = mesas.filter((m) => m.id !== comanda?.mesaId);
  const destino = outras.find((m) => m.id === escolhida);
  const atual = mesas.find((m) => m.id === comanda?.mesaId);

  async function mover() {
    if (!comanda || !destino) {
      return;
    }

    setErro(undefined);
    setEnviando(true);
    const resultado = await trocarMesa(comanda.id, destino.id, comanda.versao);
    setEnviando(false);
    if (resultado.ok) {
      await Promise.all([
        queryClient.invalidateQueries({ queryKey: ["comandas"] }),
        queryClient.invalidateQueries({ queryKey: ["mesas"] }),
      ]);
      onMovida(comanda, destino);
      return;
    }

    setErro(resultado.mensagem);
    if (resultado.recarregar) {
      await Promise.all([
        queryClient.invalidateQueries({ queryKey: ["comandas"] }),
        queryClient.invalidateQueries({ queryKey: ["mesas"] }),
      ]);
    }
  }

  return (
    <BottomSheet
      open={comanda !== null}
      onOpenChange={(aberta) => {
        if (!aberta) {
          onClose();
        }
      }}
      title="Trocar de mesa"
      description={
        comanda
          ? `${comanda.numeroTexto}${atual ? ` está em ${nomeDaMesa(atual)}` : ""}. Escolha a nova mesa.`
          : undefined
      }
      error={erro}
      loading={enviando}
      footer={
        <>
          <Button variant="secondary" onClick={onClose} disabled={enviando}>
            Voltar
          </Button>
          <Button loading={enviando} disabled={destino === undefined} onClick={() => void mover()}>
            {destino ? `Mover para ${nomeDaMesa(destino)}` : "Mover"}
          </Button>
        </>
      }
    >
      {outras.length === 0 ? (
        <p className="mesa-nota">Não há outra mesa cadastrada nesta unidade.</p>
      ) : (
        <ul className="mesa-opcoes" aria-label="Mesas disponíveis">
          {outras.map((mesa) => (
            <li key={mesa.id}>
              <button
                type="button"
                className="mesa-opcao"
                aria-pressed={escolhida === mesa.id}
                disabled={enviando}
                onClick={() => {
                  setEscolhida(mesa.id);
                  setErro(undefined);
                }}
              >
                <span className="f4-num">{nomeDaMesa(mesa)}</span>
                <StatusBadge status={mesa.status} />
              </button>
            </li>
          ))}
        </ul>
      )}
    </BottomSheet>
  );
}
