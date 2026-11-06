import type { ReactNode } from "react";

export interface NoticeProps {
  tone: "info" | "warning" | "error";
  title?: string | undefined;
  children: ReactNode;
  /** One action ("Tentar novamente"). */
  action?: ReactNode;
}

/** A banner for a state of the screen (blocked, offline, failed load). An error is announced at once (alert); the rest politely (status). The word carries the meaning, not only the color. */
export function Notice({ tone, title, children, action }: NoticeProps) {
  return (
    <div className="f4-notice" data-tone={tone} role={tone === "error" ? "alert" : "status"}>
      <div>
        {title ? <p className="f4-notice-title">{title}</p> : null}
        <p className="f4-notice-text">{children}</p>
      </div>
      {action ? <div className="f4-notice-action">{action}</div> : null}
    </div>
  );
}

/** Placeholder rows while a list loads (never a centered spinner). */
export function Skeleton({ rows = 3, label = "Carregando" }: { rows?: number; label?: string }) {
  return (
    <div role="status" aria-label={label} aria-busy="true">
      {Array.from({ length: rows }, (_, i) => (
        <div key={i} className="f4-skeleton" aria-hidden="true" />
      ))}
    </div>
  );
}
