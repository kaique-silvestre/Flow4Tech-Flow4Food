import { Icon, type IconName } from "./Icon";

export type ComandaStatus = "aberta" | "aguardando-pagamento" | "parada" | "fechada";

/** The derived status of a table on the map: free (hollow ring), occupied (filled circle), waiting for the bill (triangle). */
export type MesaStatus = "livre" | "ocupada" | "aguardando-conta";

const WORDS: Record<ComandaStatus | MesaStatus, string> = {
  aberta: "Aberta",
  "aguardando-pagamento": "Aguardando pagamento",
  parada: "Parada",
  fechada: "Fechada",
  livre: "Livre",
  ocupada: "Ocupada",
  "aguardando-conta": "Aguardando conta",
};

export interface StatusBadgeProps {
  status: ComandaStatus | MesaStatus;
  /** Elapsed time after the word ("há 1h 12min"). */
  detail?: string | undefined;
  /** Draws an icon (dot, clock, alert, check) instead of the shaped marker; the word stays. */
  icon?: boolean | undefined;
}

const ICON_OF: Record<ComandaStatus | MesaStatus, IconName> = {
  aberta: "dot",
  "aguardando-pagamento": "clock",
  parada: "warn",
  fechada: "check",
  livre: "dot",
  ocupada: "dot",
  "aguardando-conta": "clock",
};

/** Status is never color alone: a marker whose SHAPE differs (circle, triangle, diamond, square) and always the word. */
export function StatusBadge({ status, detail, icon = false }: StatusBadgeProps) {
  return (
    <span className="f4-badge" data-status={status}>
      {icon ? <Icon name={ICON_OF[status]} size={14} /> : <span className="f4-badge-marker" aria-hidden="true" />}
      <span>
        {WORDS[status]}
        {detail ? ` ${detail}` : ""}
      </span>
    </span>
  );
}
