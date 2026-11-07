import type { Ref } from "react";
import { centsFromDigits, formatBRL } from "../format";
import { TextField, type TextFieldProps } from "./TextField";

export interface MoneyFieldProps extends Omit<TextFieldProps, "value" | "onChange" | "type" | "inputMode" | "ref"> {
  /** What was typed: digits only, read as cents ("" means nothing typed yet; "0" is a real zero). */
  digits: string;
  onDigitsChange: (digits: string) => void;
  ref?: Ref<HTMLInputElement> | undefined;
}

const MAX_DIGITS = 9;

/** An amount in BRL typed as digits (the cents fill from the right, like a cash register): "1" is R$ 0,01, "1250" is R$ 12,50. Shown formatted, with tabular figures, in a numeric keypad. */
export function MoneyField({ digits, onDigitsChange, ref, ...rest }: MoneyFieldProps) {
  return (
    <TextField
      {...rest}
      ref={ref}
      className={["f4-money", rest.className].filter(Boolean).join(" ")}
      inputMode="numeric"
      autoComplete="off"
      value={digits === "" ? "" : formatBRL(centsFromDigits(digits))}
      placeholder={formatBRL(0)}
      onChange={(event) => {
        const only = event.target.value
          .replace(/\D/g, "")
          .replace(/^0+(?=\d)/, "")
          .slice(0, MAX_DIGITS);
        onDigitsChange(only);
      }}
    />
  );
}
