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
}

/** Status is never color alone: a marker whose SHAPE differs (circle, triangle, diamond, square) and always the word. */
export function StatusBadge({ status, detail }: StatusBadgeProps) {
  return (
    <span className="f4-badge" data-status={status}>
      <span className="f4-badge-marker" aria-hidden="true" />
      <span>
        {WORDS[status]}
        {detail ? ` ${detail}` : ""}
      </span>
    </span>
  );
}
