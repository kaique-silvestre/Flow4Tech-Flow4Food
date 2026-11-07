import type { InputHTMLAttributes, ReactNode, Ref } from "react";
import { useId } from "react";

export interface TextFieldProps extends Omit<InputHTMLAttributes<HTMLInputElement>, "id"> {
  label: string;
  /** Help under the field (what to type, where it is saved). */
  hint?: string | undefined;
  /** A refusal for THIS field: shown under it, linked to it, and outlines it. */
  error?: string | undefined;
  /** An action inside the field (show password); it keeps a 44px target. */
  trailing?: ReactNode;
  /** A request that uses this field is in flight. */
  loading?: boolean;
  ref?: Ref<HTMLInputElement> | undefined;
}

/** Label above the input (never a placeholder as the label), 48px high, error and hint linked with aria-describedby. */
export function TextField({
  label,
  hint,
  error,
  trailing,
  loading = false,
  className,
  disabled,
  ref,
  ...rest
}: TextFieldProps) {
  const id = useId();
  const hintId = `${id}-hint`;
  const errorId = `${id}-error`;
  const describedBy =
    [error ? errorId : null, hint ? hintId : null, rest["aria-describedby"]].filter(Boolean).join(" ") || undefined;
  return (
    <div
      className={["f4-field", className].filter(Boolean).join(" ")}
      data-state={error ? "error" : loading ? "loading" : "default"}
    >
      <label htmlFor={id} className="f4-field-label">
        {label}
      </label>
      <div className="f4-field-control">
        <input
          {...rest}
          ref={ref}
          id={id}
          className="f4-input"
          disabled={disabled || loading}
          aria-invalid={error ? true : undefined}
          aria-describedby={describedBy}
          aria-busy={loading || undefined}
        />
        {trailing ? <span className="f4-field-trailing">{trailing}</span> : null}
      </div>
      {hint ? (
        <p id={hintId} className="f4-field-hint">
          {hint}
        </p>
      ) : null}
      {error ? (
        <p id={errorId} role="alert" className="f4-field-error">
          {error}
        </p>
      ) : null}
    </div>
  );
}
