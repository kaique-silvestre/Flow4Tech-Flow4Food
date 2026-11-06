import { existsSync, readFileSync } from "node:fs";
import { join } from "node:path";

export interface Seed {
  organizacaoA: string;
  unidadeCentroA: string;
  unidadePraiaA: string;
  organizacaoB: string;
  contas: Record<string, string>;
}

const arquivo = join(import.meta.dirname, "..", "e2e-results", "seed.json");

/** The ids written by the seed (docs/agents/project.md, "e2e seed"); null when the seed was not run. */
export function lerSeed(): Seed | null {
  return existsSync(arquivo) ? (JSON.parse(readFileSync(arquivo, "utf8")) as Seed) : null;
}

/** The throwaway password the seed set (never a real credential). */
export const SENHA = process.env["E2E_SEED_PASSWORD"] ?? "e2e-local-throwaway-password";
