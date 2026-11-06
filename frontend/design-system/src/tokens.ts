/**
 * Token NAMES are stable; VALUES are provisional until the first real-device use (docs/design/DESIGN.md).
 * The CSS variables live in styles.css; this module is the typed list of their names, used by components and by the tests that keep the two in step.
 */
export const colorTokens = [
  "bg",
  "surface",
  "surface-2",
  "hover",
  "ink",
  "ink-muted",
  "ink-faint",
  "accent",
  "accent-ink",
  "border",
  "border-strong",
  "success",
  "success-soft",
  "warning",
  "warning-ink",
  "warning-soft",
  "error",
  "error-soft",
  "info",
  "scrim",
] as const;

export const spaceTokens = ["1", "2", "3", "4", "6", "8", "12"] as const;

export const radiusTokens = ["sm", "md", "lg", "sheet", "pill"] as const;

export const motionTokens = ["fast", "base", "ease"] as const;

export type ColorToken = (typeof colorTokens)[number];

/** Space scale in px: 4 / 8 / 12 / 16 / 24 / 32 / 48. */
export const spacePx = { "1": 4, "2": 8, "3": 12, "4": 16, "6": 24, "8": 32, "12": 48 } as const;

/** Minimum sizes in px (docs/design/DESIGN.md "touch"). */
export const touch = { target: 44, primary: 52, key: 64 } as const;

export function cssVar(name: string): string {
  return `var(--f4-${name})`;
}
