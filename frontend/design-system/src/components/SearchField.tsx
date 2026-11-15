import type { InputHTMLAttributes, Ref } from "react";
import { useId } from "react";
import { Icon } from "./Icon";

export interface SearchFieldProps extends Omit<InputHTMLAttributes<HTMLInputElement>, "id" | "type"> {
  /** The accessible name; it is not drawn (the placeholder and the icon say what the field is). */
  label: string;
  ref?: Ref<HTMLInputElement> | undefined;
}

/** A search input with a magnifier: 48px high, the label kept for screen readers, a native `search` type so the keyboard shows its search key. */
export function SearchField({ label, className, ref, ...rest }: SearchFieldProps) {
  const id = useId();
  return (
    <div className={["f4-search", className].filter(Boolean).join(" ")}>
      <label htmlFor={id} className="f4-visually-hidden">
        {label}
      </label>
      <span className="f4-search-icon">
        <Icon name="search" />
      </span>
      <input {...rest} ref={ref} id={id} type="search" inputMode="search" autoComplete="off" className="f4-input" />
    </div>
  );
}
