import type { ReactNode } from "react";
import { formatBRL } from "../format";

export interface RoundBarProps {
  /** Number of units staged and not sent. The bar does not exist at zero. */
  itens: number;
  /** Total of the staged round, in cents. */
  totalCentavos: number;
  /** The one accent action of the screen. */
  action: ReactNode;
}

/** The signature of the comanda screen: a sticky bar that appears with the first staged item and keeps "staged" apart from "sent" at a glance. */
export function RoundBar({ itens, totalCentavos, action }: RoundBarProps) {
  if (itens <= 0) {
    return null;
  }

  return (
    <div className="f4-roundbar" role="region" aria-label="Rodada não enviada">
      <p className="f4-roundbar-text">
        <strong className="f4-num">
          {itens} {itens === 1 ? "item não enviado" : "itens não enviados"}
        </strong>
        <span className="f4-num"> · {formatBRL(totalCentavos)}</span>
        <span className="f4-roundbar-note">Cozinha e bar só recebem ao enviar</span>
      </p>
      <div className="f4-roundbar-action">{action}</div>
    </div>
  );
}
