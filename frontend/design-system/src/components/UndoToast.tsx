import { useEffect, useRef, useState } from "react";

export interface UndoToastProps {
  /** What happened ("Rodada enviada"). */
  message: string;
  /** How long the undo is offered, in ms (the server window, 8 seconds). */
  durationMs: number;
  /** The undo button was pressed (only offered while the window is open). */
  onUndo: () => void;
  /** The window ended: the action is gone and the toast should be removed. */
  onExpire: () => void;
  /** An undo request is in flight. */
  loading?: boolean;
}

/**
 * "Rodada enviada · Desfazer" with a linear bar that runs the window down. It states the seconds left in words (not only the bar), and under reduced motion the bar stays still
 * while the words keep counting. When the window ends the toast reports it and the action is gone.
 */
export function UndoToast({ message, durationMs, onUndo, onExpire, loading = false }: UndoToastProps) {
  const [restanteMs, setRestanteMs] = useState(durationMs);
  const expirou = useRef(onExpire);
  expirou.current = onExpire;

  useEffect(() => {
    const fim = Date.now() + durationMs;
    setRestanteMs(durationMs);
    const timer = window.setInterval(() => {
      const resta = fim - Date.now();
      if (resta <= 0) {
        window.clearInterval(timer);
        setRestanteMs(0);
        expirou.current();
        return;
      }

      setRestanteMs(resta);
    }, 250);
    return () => window.clearInterval(timer);
  }, [durationMs]);

  const segundos = Math.max(0, Math.ceil(restanteMs / 1000));
  return (
    <div className="f4-toast" role="status" aria-live="polite">
      <div className="f4-toast-row">
        <span>
          {message} <span className="f4-num f4-toast-sec">({segundos} s)</span>
        </span>
        <button
          type="button"
          className="f4-toast-undo"
          onClick={onUndo}
          disabled={loading || restanteMs <= 0}
          aria-busy={loading || undefined}
        >
          Desfazer
        </button>
      </div>
      <div className="f4-toast-track" aria-hidden="true">
        <div className="f4-toast-bar" style={{ animationDuration: `${durationMs}ms` }} />
      </div>
    </div>
  );
}
