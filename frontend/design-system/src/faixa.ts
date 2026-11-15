import { useSyncExternalStore } from "react";

/** The only width cuts of the app (ADR-019, px CSS, mobile-first). No screen uses another number. */
export const CORTE_TABLET = 500;
export const CORTE_COMPUTADOR = 1000;

export type Faixa = "celular" | "tablet" | "computador";

export function faixaDaLargura(largura: number): Faixa {
  if (largura < CORTE_TABLET) {
    return "celular";
  }

  return largura < CORTE_COMPUTADOR ? "tablet" : "computador";
}

/** What the person chose for the side menu on this device; `null` means nobody chose and the band decides. */
export type EscolhaDoMenu = "trilho" | "completo" | null;

/** True when the side menu is the 72 px rail. The phone has no side menu (it has the bottom tab bar); a saved choice beats the automatic one (the prototype's `flow.sidebar`). */
export function menuRecolhido(faixa: Faixa, escolha: EscolhaDoMenu): boolean {
  if (faixa === "celular") {
    return false;
  }

  return escolha === null ? faixa === "tablet" : escolha === "trilho";
}

function assinar(aviso: () => void) {
  window.addEventListener("resize", aviso);
  return () => window.removeEventListener("resize", aviso);
}

/** The band of the window right now, live on resize and rotation. */
export function useFaixa(): Faixa {
  return useSyncExternalStore(
    assinar,
    () => faixaDaLargura(window.innerWidth),
    () => "celular",
  );
}
