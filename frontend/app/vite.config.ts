import react from "@vitejs/plugin-react";
import type { ProxyOptions } from "vite";
import { VitePWA } from "vite-plugin-pwa";
import { defineConfig } from "vitest/config";

/** The API routes the SPA calls; in dev and preview they are proxied to the API so the browser sees ONE origin (no CORS, cookies and the CSRF guard keep working). */
const apiTarget = process.env["API_URL"] ?? "http://127.0.0.1:8080";
const apiPaths =
  "^/(login|refresh|logout|version|health|unidades|sessao|sessoes|caixa|cardapio|comandas|mesas|configuracoes|conta|contas|convites|recuperacao)(/|$)";
const proxy: Record<string, ProxyOptions> = { [apiPaths]: { target: apiTarget, changeOrigin: false } };

export default defineConfig({
  plugins: [
    react(),
    VitePWA({
      registerType: "autoUpdate",
      includeAssets: ["icon.svg", "apple-touch-icon.png"],
      manifest: {
        name: "Flow4Tech Salão",
        short_name: "Salão",
        description: "Comandas, caixa e recebimento do salão.",
        lang: "pt-BR",
        start_url: "/salao",
        scope: "/",
        display: "standalone",
        orientation: "portrait",
        background_color: "#f7f7f8",
        theme_color: "#f7f7f8",
        icons: [
          { src: "/icon-192.png", sizes: "192x192", type: "image/png" },
          { src: "/icon-512.png", sizes: "512x512", type: "image/png" },
          { src: "/icon-512.png", sizes: "512x512", type: "image/png", purpose: "maskable" },
        ],
      },
      workbox: {
        globPatterns: ["**/*.{js,css,html,svg,png,woff2}"],
        navigateFallback: "/index.html",
        // The API is never answered from the cache by the shell: a stale bill would be worse than an error.
        navigateFallbackDenylist: [new RegExp(apiPaths)],
      },
    }),
  ],
  server: { proxy },
  preview: { proxy },
  test: {
    environment: "jsdom",
    globals: true,
    setupFiles: ["./src/test-setup.ts"],
    css: false,
    include: ["src/**/*.test.{ts,tsx}"],
    coverage: {
      provider: "v8",
      include: ["src/**"],
      exclude: ["src/**/*.test.{ts,tsx}", "src/test-setup.ts", "src/**/*.d.ts"],
      reporter: ["text-summary", "json-summary", "lcov"],
      // Floor, not a goal (F4-0103): measured 2026-11-07 at 87 / 80 / 93 / 87 (statements / branches / functions / lines). Raise it when the real number rises.
      thresholds: { statements: 85, branches: 77, functions: 90, lines: 85 },
    },
  },
});
