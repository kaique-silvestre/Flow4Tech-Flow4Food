import { defineConfig } from "vitest/config";

export default defineConfig({
  test: {
    coverage: {
      provider: "v8",
      include: ["src/**"],
      // schema.ts is generated from openapi.json (never edited by hand), index.test.ts is the test itself.
      exclude: ["src/schema.ts", "src/**/*.test.ts"],
      reporter: ["text-summary", "json-summary", "lcov"],
      // Floor, not a goal (F4-0103): the package is one small file, measured at 100%.
      thresholds: { statements: 90, branches: 90, functions: 90, lines: 90 },
    },
  },
});
