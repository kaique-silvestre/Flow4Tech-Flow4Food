const DIGITS = ["1", "2", "3", "4", "5", "6", "7", "8", "9"] as const;

export interface KeypadProps {
  onDigit: (digit: string) => void;
  onBackspace: () => void;
  onCancel: () => void;
  disabled?: boolean;
  loading?: boolean;
  /** Message for a refused entry; also outlines the pad. */
  error?: string | undefined;
}

/** 3x4 pad (no native input, so no phone keyboard covers the sheet): 1-9, Apagar, 0, Cancelar. Every key is at least 64px high. */
export function Keypad({ onDigit, onBackspace, onCancel, disabled = false, loading = false, error }: KeypadProps) {
  const off = disabled || loading;
  return (
    <div>
      <div className="f4-keypad" role="group" aria-label="Teclado numérico" aria-busy={loading || undefined} data-state={error ? "error" : "default"}>
        {DIGITS.map((digit) => (
          <button key={digit} type="button" className="f4-key" disabled={off} onClick={() => onDigit(digit)}>
            {digit}
          </button>
        ))}
        <button type="button" className="f4-key" data-kind="action" disabled={off} onClick={onBackspace}>
          Apagar
        </button>
        <button type="button" className="f4-key" disabled={off} onClick={() => onDigit("0")}>
          0
        </button>
        <button type="button" className="f4-key" data-kind="action" disabled={loading} onClick={onCancel}>
          Cancelar
        </button>
      </div>
      {error ? (
        <p role="alert" className="f4-field-error">
          {error}
        </p>
      ) : null}
    </div>
  );
}
