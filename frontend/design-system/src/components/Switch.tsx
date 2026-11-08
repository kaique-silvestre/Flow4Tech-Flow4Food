import type { ButtonHTMLAttributes } from "react";

export interface SwitchProps
  extends Omit<ButtonHTMLAttributes<HTMLButtonElement>, "children" | "onChange" | "role" | "aria-checked"> {
  checked: boolean;
  onCheckedChange: (checked: boolean) => void;
  /** The visible name of the setting; it is also the accessible name. */
  label: string;
  /** A request that changes this setting is in flight: the switch holds still and says it is busy. */
  loading?: boolean;
  error?: boolean;
}

/** An on/off setting that is saved at once (not a form field): a native button with role switch, so the keyboard (Space/Enter) and the screen reader work without extra code. The state is the thumb position plus the announced value, never color alone. */
export function Switch({
  checked,
  onCheckedChange,
  label,
  loading = false,
  error = false,
  disabled,
  className,
  type = "button",
  ...rest
}: SwitchProps) {
  return (
    <button
      {...rest}
      type={type}
      role="switch"
      aria-checked={checked}
      aria-busy={loading || undefined}
      aria-invalid={error || undefined}
      data-state={error ? "error" : loading ? "loading" : "default"}
      className={["f4-switch", className].filter(Boolean).join(" ")}
      disabled={disabled || loading}
      onClick={() => onCheckedChange(!checked)}
    >
      <span className="f4-switch-track" aria-hidden="true">
        <span className="f4-switch-thumb" />
      </span>
      <span className="f4-switch-label">{label}</span>
    </button>
  );
}
