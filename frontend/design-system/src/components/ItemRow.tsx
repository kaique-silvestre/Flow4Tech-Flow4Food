import type { ReactNode } from "react";
import { formatBRL } from "../format";

export interface ItemRowProps {
  quantity: number;
  name: string;
  /** Small second line: the note ("sem gelo"). Truncated with an ellipsis; price and quantity never are. */
  modifier?: string | undefined;
  /** Unit price in cents; the row shows quantity times price. */
  priceCents: number;
  /** `lancado` counts in the bill; `cancelado` is struck through and leaves the total. */
  state?: "lancado" | "cancelado";
  /** A write for this row is in flight. */
  loading?: boolean;
  /** The write failed: the row says so and offers the retry. */
  error?: string | undefined;
  onRetry?: (() => void) | undefined;
  /** Right-side action ("Cancelar"). */
  action?: ReactNode;
}

export function ItemRow({ quantity, name, modifier, priceCents, state = "lancado", loading = false, error, onRetry, action }: ItemRowProps) {
  const rowState = error ? "error" : loading ? "loading" : state;
  return (
    <li className="f4-row" data-state={rowState} aria-busy={loading || undefined}>
      <span className="f4-row-qty f4-num">{quantity}×</span>
      <span className="f4-row-name">
        {name}
        {modifier ? <span className="f4-row-mod">{modifier}</span> : null}
      </span>
      <span className="f4-row-price f4-num">{formatBRL(priceCents * quantity)}</span>
      {state === "cancelado" ? (
        <span className="f4-row-extra f4-visually-hidden">Cancelado</span>
      ) : action || error ? (
        <span className="f4-row-extra">
          {error ? (
            <>
              <span role="alert" className="f4-field-error">
                {error}
              </span>
              {onRetry ? (
                <button type="button" className="f4-row-action" onClick={onRetry}>
                  Tentar novamente
                </button>
              ) : null}
            </>
          ) : (
            action
          )}
        </span>
      ) : null}
    </li>
  );
}

export interface StepperProps {
  value: number;
  onChange: (value: number) => void;
  min?: number;
  max?: number;
  label: string;
}

/** Quantity stepper for a staged row: − and + at 44x44 with 8px between them. */
export function Stepper({ value, onChange, min = 1, max = 999, label }: StepperProps) {
  return (
    <span className="f4-stepper" role="group" aria-label={label}>
      <button type="button" aria-label={`Diminuir ${label}`} disabled={value <= min} onClick={() => onChange(value - 1)}>
        −
      </button>
      <span className="f4-num" aria-live="polite">
        {value}
      </span>
      <button type="button" aria-label={`Aumentar ${label}`} disabled={value >= max} onClick={() => onChange(value + 1)}>
        +
      </button>
    </span>
  );
}
