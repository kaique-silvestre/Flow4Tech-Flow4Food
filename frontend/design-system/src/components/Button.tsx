import type { ButtonHTMLAttributes, ReactNode } from "react";
import { useId } from "react";

export interface ButtonProps extends Omit<ButtonHTMLAttributes<HTMLButtonElement>, "children"> {
  variant?: "primary" | "secondary";
  /** A request is in flight: shows a spinner inside the button and ignores clicks. */
  loading?: boolean;
  /** An inline message under the button (a failed request); the button itself stays usable to retry. */
  error?: string | undefined;
  children: ReactNode;
}

export function Button({ variant = "primary", loading = false, error, disabled, className, children, type = "button", ...rest }: ButtonProps) {
  const messageId = useId();
  const classes = ["f4-btn", className].filter(Boolean).join(" ");
  return (
    <>
      <button
        {...rest}
        type={type}
        className={classes}
        data-variant={variant}
        data-state={error ? "error" : loading ? "loading" : "default"}
        aria-busy={loading || undefined}
        aria-describedby={error ? messageId : rest["aria-describedby"]}
        disabled={disabled || loading}
      >
        {loading ? <span className="f4-spinner" aria-hidden="true" /> : null}
        {children}
      </button>
      {error ? (
        <p id={messageId} role="alert" className="f4-field-error">
          {error}
        </p>
      ) : null}
    </>
  );
}
