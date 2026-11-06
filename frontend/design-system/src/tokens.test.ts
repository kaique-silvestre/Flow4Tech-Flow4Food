import { readFileSync } from "node:fs";
import { join } from "node:path";
import { describe, expect, it } from "vitest";
import { colorTokens, motionTokens, radiusTokens, spacePx, spaceTokens, touch } from "./tokens";

const css = readFileSync(join(import.meta.dirname, "styles.css"), "utf8");
const light = css.slice(css.indexOf(":root {"), css.indexOf(':root[data-theme="dark"]'));
const dark = css.slice(css.indexOf(':root[data-theme="dark"]'), css.indexOf("@media (prefers-color-scheme: dark)"));

describe("design tokens", () => {
  it("declares every color token in light mode as OKLCH", () => {
    for (const name of colorTokens) {
      expect(light, `--f4-${name} (light)`).toMatch(new RegExp(`--f4-${name}: oklch\\(`));
    }
  });

  it("redefines the surfaces, ink, accent and statuses for dark mode", () => {
    for (const name of ["bg", "surface", "ink", "ink-muted", "accent", "accent-ink", "border", "success", "warning", "error", "scrim"]) {
      expect(dark, `--f4-${name} (dark)`).toMatch(new RegExp(`--f4-${name}: oklch\\(`));
    }
  });

  it("follows the system dark theme unless the light theme is forced", () => {
    expect(css).toContain('@media (prefers-color-scheme: dark)');
    expect(css).toContain(':root:not([data-theme="light"])');
  });

  it("declares the space, radius and motion scales with stable names", () => {
    for (const n of spaceTokens) {
      expect(css).toContain(`--f4-space-${n}: ${spacePx[n]}px`);
    }
    for (const n of radiusTokens) {
      expect(css).toMatch(new RegExp(`--f4-radius-${n}: \\d+px`));
    }
    for (const n of motionTokens) {
      expect(css).toMatch(new RegExp(`--f4-motion-${n}:`));
    }
    expect(css).toContain("cubic-bezier(0.16, 1, 0.3, 1)");
  });

  it("self-hosts the font and never asks a font service at runtime", () => {
    expect(css).toContain("@fontsource-variable/geist");
    expect(css).not.toMatch(/fonts\.(googleapis|gstatic)\.com/);
  });

  it("keeps the touch minimums of the design", () => {
    expect(touch).toEqual({ target: 44, primary: 52, key: 64 });
    expect(css).toMatch(/\.f4-key \{[^}]*min-height: 64px/s);
    expect(css).toMatch(/data-variant="primary"\] \{[^}]*min-height: 52px/s);
  });
});
