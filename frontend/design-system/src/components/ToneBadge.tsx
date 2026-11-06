import type { ReactNode } from "react";

export type Tone = "success" | "warning" | "alert" | "neutral";

/** Same markers and colors as the status badge, for a result that is not a comanda status ("Confere", "Falta R$ 5,00"). The word always comes with the marker; the marker shape differs per tone. */
const STATUS_OF_TONE = { success: "aberta", warning: "aguardando-pagamento", alert: "parada", neutral: "fechada" } as const;

export function ToneBadge({ tone, children }: { tone: Tone; children: ReactNode }) {
  return (
    <span className="f4-badge" data-status={STATUS_OF_TONE[tone]} data-tone={tone}>
      <span className="f4-badge-marker" aria-hidden="true" />
      <span>{children}</span>
    </span>
  );
}
