import { defineConfig } from "vitest/config";

export default defineConfig({
  test: {
    environment: "jsdom",
    globals: true,
    setupFiles: ["./src/test-setup.ts"],
    css: false,
    coverage: {
      provider: "v8",
      include: ["src/**"],
      exclude: ["src/**/*.test.{ts,tsx}", "src/test-setup.ts", "src/**/*.d.ts"],
      reporter: ["text-summary", "json-summary", "lcov"],
      // Floor, not a goal (F4-0103): measured 2026-11-07 at 98.7 / 99 / 97 / 98.7 (statements / branches / functions / lines). Raise it when the real number rises.
      thresholds: { statements: 95, branches: 95, functions: 93, lines: 95 },
    },
  },
});
