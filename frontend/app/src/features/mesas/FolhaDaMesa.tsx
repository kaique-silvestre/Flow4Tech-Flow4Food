import { BottomSheet, Button } from "@flow4tech/design-system";
import { useNavigate } from "@tanstack/react-router";
import type { Comanda } from "../comandas/tipos";
import { type MesaDoMapa, nomeDaMesa } from "./tipos";

interface Props {
  /** The occupied table whose comandas are shown. Null closes the sheet. */
  mesa: MesaDoMapa | null;
  /** The open comandas of the unit the caller may see (the sheet keeps the ones on this table). */
  comandas: readonly Comanda[];
  podeAbrir: boolean;
  podeTrocar: boolean;
  onClose: () => void;
  onAbrirOutra: (mesa: MesaDoMapa) => void;
  onTrocar: (comanda: Comanda) => void;
}

/** An occupied table: its comandas (tap one to open it, "Trocar de mesa" to move it) and, when allowed, "Abrir outra comanda" on the same table. */
export function FolhaDaMesa({ mesa, comandas, podeAbrir, podeTrocar, onClose, onAbrirOutra, onTrocar }: Props) {
  const navigate = useNavigate();
  const daMesa = mesa ? comandas.filter((c) => c.mesaId === mesa.id) : [];
  const escondidas = mesa ? mesa.comandasAbertas - daMesa.length : 0;

  return (
    <BottomSheet
      open={mesa !== null}
      onOpenChange={(aberta) => {
        if (!aberta) {
          onClose();
        }
      }}
      title={mesa ? nomeDaMesa(mesa) : "Mesa"}
      description={
        mesa
          ? `${mesa.comandasAbertas} ${mesa.comandasAbertas === 1 ? "comanda aberta" : "comandas abertas"} nesta mesa.`
          : undefined
      }
      footer={
        <>
          <Button variant="secondary" onClick={onClose}>
            Fechar
          </Button>
          {podeAbrir && mesa ? <Button onClick={() => onAbrirOutra(mesa)}>Abrir outra comanda</Button> : null}
        </>
      }
    >
      {daMesa.length > 0 ? (
        <ul className="mesa-comandas" aria-label="Comandas desta mesa">
          {daMesa.map((comanda) => (
            <li key={comanda.id} className="mesa-comanda">
              <Button
                variant="secondary"
                className="mesa-comanda-ir"
                onClick={() => {
                  onClose();
                  void navigate({ to: "/salao/comanda/$comandaId", params: { comandaId: comanda.id } });
                }}
              >
                {comanda.numeroTexto} · {comanda.identificacao}
              </Button>
              {podeTrocar ? (
                <Button
                  variant="secondary"
                  aria-label={`Trocar de mesa a comanda ${comanda.numeroTexto}`}
                  onClick={() => onTrocar(comanda)}
                >
                  Trocar de mesa
                </Button>
              ) : null}
            </li>
          ))}
        </ul>
      ) : null}
      {escondidas > 0 ? (
        <p className="mesa-nota">
          {daMesa.length === 0
            ? "As comandas desta mesa são de outra pessoa."
            : `Mais ${escondidas} ${escondidas === 1 ? "comanda é" : "comandas são"} de outra pessoa.`}
        </p>
      ) : null}
    </BottomSheet>
  );
}
