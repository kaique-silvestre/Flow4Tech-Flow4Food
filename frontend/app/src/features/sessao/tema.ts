import { useSyncExternalStore } from "react";

export type Tema = "claro" | "escuro";

const CHAVE = "f4t-tema";
const ouvintes = new Set<() => void>();

function guardado(): Tema | null {
  try {
    const valor = window.localStorage.getItem(CHAVE);
    return valor === "claro" || valor === "escuro" ? valor : null;
  } catch {
    return null;
  }
}

function doSistema(): Tema {
  return window.matchMedia?.("(prefers-color-scheme: dark)").matches ? "escuro" : "claro";
}

/** The theme in force: what is on the page (the choice of this session), else what the person saved on this device, else what the phone prefers (which the design system's tokens follow by themselves). */
export function temaAtual(): Tema {
  const naPagina = document.documentElement.dataset["theme"];
  return naPagina === "dark" ? "escuro" : naPagina === "light" ? "claro" : (guardado() ?? doSistema());
}

/** Puts the theme on the page (`data-theme` is what the design system's tokens read). */
function aplicarTema(tema: Tema) {
  document.documentElement.dataset["theme"] = tema === "escuro" ? "dark" : "light";
}

/**
 * Called once before the first render so the screen never flashes the wrong theme. Only a choice the person made is put on the page: without one the attribute stays off and the tokens follow the
 * phone's own setting live (the design system's `prefers-color-scheme` rule).
 */
export function aplicarTemaSalvo() {
  const escolhido = guardado();
  if (escolhido !== null) {
    aplicarTema(escolhido);
  }
}

export function alternarTema() {
  const novo: Tema = temaAtual() === "escuro" ? "claro" : "escuro";
  try {
    window.localStorage.setItem(CHAVE, novo);
  } catch {
    // Private mode: the choice lasts until the page closes (the attribute below), it just is not remembered.
  }

  aplicarTema(novo);
  for (const ouvinte of ouvintes) {
    ouvinte();
  }
}

/** The current theme and the switch; the choice is per device, never sent to the server. */
export function useTema(): { tema: Tema; alternar: () => void } {
  const tema = useSyncExternalStore((ouvinte) => {
    ouvintes.add(ouvinte);
    return () => ouvintes.delete(ouvinte);
  }, temaAtual);
  return { tema, alternar: alternarTema };
}
