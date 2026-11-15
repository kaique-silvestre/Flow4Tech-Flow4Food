import type { Faixa } from "@flow4tech/design-system";
import { createContext, useContext, useEffect, useRef } from "react";

/**
 * Keyboard shortcuts of the comandas screen, computer only (ADR-019). The tablet has none: there is no simple, reliable way to know a physical keyboard is attached, so it waits for a decision.
 * Reference: .scratch/S-00-novo-sistema/prototipos/index.html#m=web (keydown handler, l. 7350-7374). Single keys are off while a field has focus, a dialog is open or a modifier is held.
 */
export type Atalho = "nova" | "buscar" | "menu" | "fechar";

export interface TeclaApertada {
  key: string;
  ctrlKey: boolean;
  metaKey: boolean;
  altKey: boolean;
}

export interface ContextoDoAtalho {
  faixa: Faixa;
  /** Focus is in an input, select, textarea or editable element. */
  emCampo: boolean;
  /** A sheet or dialog is open: it owns the keyboard (its own Esc closes it). */
  dialogoAberto: boolean;
  /** A comanda is open in the side panel (Esc closes it). */
  painelAberto: boolean;
}

export function atalhoDaTecla(tecla: TeclaApertada, contexto: ContextoDoAtalho): Atalho | null {
  if (contexto.faixa !== "computador") {
    return null;
  }

  const letra = tecla.key.toLowerCase();
  if ((tecla.ctrlKey || tecla.metaKey) && !tecla.altKey && letra === "k") {
    return contexto.dialogoAberto ? null : "buscar";
  }

  if (contexto.emCampo || contexto.dialogoAberto) {
    return null;
  }

  if (tecla.key === "Escape") {
    return contexto.painelAberto ? "fechar" : null;
  }

  if (tecla.ctrlKey || tecla.metaKey || tecla.altKey) {
    return null;
  }

  if (tecla.key === "[") {
    return "menu";
  }

  return letra === "n" ? "nova" : null;
}

const QUANTIDADE_RAPIDA_MAXIMA = 99;

/** "3 chope" is three of the product that "chope" finds; anything else is just a search. */
export function lancamentoRapido(texto: string): { quantidade: number; termo: string } | null {
  const achado = /^(\d{1,3})\s+(\S.*)$/.exec(texto.trim());
  const quantidade = Number(achado?.[1]);
  if (!achado?.[2] || quantidade < 1 || quantidade > QUANTIDADE_RAPIDA_MAXIMA) {
    return null;
  }

  return { quantidade, termo: achado[2].trim() };
}

/** Lets the shell hand the screen the way to fold the side menu (`[`); outside the shell there is nothing to fold. */
export const AlternarMenuContext = createContext<(() => void) | null>(null);

export function useAtalhosDaTela(
  faixa: Faixa,
  painelAberto: boolean,
  acoes: Record<Exclude<Atalho, "menu">, () => void>,
) {
  const alternarMenu = useContext(AlternarMenuContext);
  const atual = useRef({ faixa, painelAberto, acoes, alternarMenu });
  atual.current = { faixa, painelAberto, acoes, alternarMenu };

  useEffect(() => {
    const ouvir = (e: KeyboardEvent) => {
      const { faixa, painelAberto, acoes, alternarMenu } = atual.current;
      const alvo = e.target instanceof Element ? e.target : null;
      const atalho = atalhoDaTecla(e, {
        faixa,
        emCampo: alvo?.closest("input, select, textarea, [contenteditable]") != null,
        dialogoAberto: document.querySelector("[role='dialog']") !== null,
        painelAberto,
      });
      if (atalho === null) {
        return;
      }

      e.preventDefault();
      if (atalho === "menu") {
        alternarMenu?.();
      } else {
        acoes[atalho]();
      }
    };
    // Capture on the window: it runs before a dialog's own Esc handler closes it, so the same Esc never closes the dialog and the panel too.
    window.addEventListener("keydown", ouvir, true);
    return () => window.removeEventListener("keydown", ouvir, true);
  }, []);
}
