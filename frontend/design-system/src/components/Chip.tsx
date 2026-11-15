import type { ButtonHTMLAttributes, ReactNode } from "react";

export interface ChipProps extends Omit<ButtonHTMLAttributes<HTMLButtonElement>, "children"> {
  selected?: boolean;
  loading?: boolean;
  error?: boolean;
  /** A tally shown after the label ("Abertas 9"); the name read aloud is "label count", the same words that are drawn. */
  count?: number;
  children: ReactNode;
}

/** A pill the user toggles (a free table, a filter). Selection is announced with `aria-pressed`, never by color alone. */
export function Chip({
  selected = false,
  loading = false,
  error = false,
  count,
  disabled,
  className,
  children,
  type = "button",
  ...rest
}: ChipProps) {
  return (
    <button
      {...rest}
      type={type}
      className={["f4-chip", className].filter(Boolean).join(" ")}
      aria-pressed={selected}
      aria-busy={loading || undefined}
      aria-invalid={error || undefined}
      data-state={error ? "error" : loading ? "loading" : "default"}
      disabled={disabled || loading}
    >
      {loading ? <span className="f4-spinner" aria-hidden="true" /> : null}
      {children}
      {count !== undefined ? <span className="f4-chip-count f4-num">{` ${count}`}</span> : null}
    </button>
  );
}
