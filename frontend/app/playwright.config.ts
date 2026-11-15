import { defineConfig } from "@playwright/test";

/**
 * Four resolutions (ADR-019, F4-0138): phone 390x844 (the design size) and 360x740 (the smallest supported), tablet 820x1180, computer 1440x900.
 * `salao-quatro-resolucoes.spec.ts` (simulated API, no backend) runs at all four; the tablet and computer projects run only it and their own spec,
 * the phones run the whole suite (the flows with the real API are the phones'). The app is served by `vite preview`
 * (the production build, so the service worker and the manifest are the real ones) which proxies the API paths to the real API (`API_URL`, default http://127.0.0.1:8080).
 * `PW_CHROMIUM` points at an already installed Chromium when the one Playwright wants is not available (cloud sessions); CI uses `playwright install chromium`.
 */
const executablePath = process.env["PW_CHROMIUM"];
const launchOptions = executablePath ? { executablePath, args: ["--no-sandbox"] } : { args: ["--no-sandbox"] };

/** `PW_PORT` moves the preview server when another worktree already holds 4173 (`reuseExistingServer` would otherwise test that worktree's build). */
const porta = process.env["PW_PORT"] ?? "4173";

export default defineConfig({
  testDir: "e2e",
  outputDir: "e2e-results/artifacts",
  fullyParallel: false,
  workers: 1,
  // Screenshot tests shoot several states in two themes; 30 s (the default) is too tight on a loaded CI runner (F4-0126).
  timeout: 60_000,
  reporter: [["list"]],
  use: {
    baseURL: `http://127.0.0.1:${porta}`,
    trace: "off",
    launchOptions,
    serviceWorkers: "allow",
  },
  projects: [
    {
      name: "phone-390x844",
      use: { viewport: { width: 390, height: 844 }, deviceScaleFactor: 2, isMobile: true, hasTouch: true },
    },
    {
      name: "phone-360x740",
      testIgnore: /salao-(tablet|computador)\.spec\.ts/,
      use: { viewport: { width: 360, height: 740 }, deviceScaleFactor: 2, isMobile: true, hasTouch: true },
    },
    // Tablet (ADR-019): only the specs that prove the side-by-side layout; the rest of the suite is the phone's.
    {
      name: "tablet-820x1180",
      testMatch: /salao-(tablet|quatro-resolucoes)\.spec\.ts/,
      use: { viewport: { width: 820, height: 1180 }, deviceScaleFactor: 1, hasTouch: true },
    },
    // Computer (ADR-019): the sidebar layout and the keyboard shortcuts.
    {
      name: "computer-1440x900",
      testMatch: /salao-(computador|quatro-resolucoes)\.spec\.ts/,
      use: { viewport: { width: 1440, height: 900 }, deviceScaleFactor: 1 },
    },
  ],
  webServer: {
    command: `pnpm run build && pnpm exec vite preview --host 127.0.0.1 --port ${porta} --strictPort`,
    url: `http://127.0.0.1:${porta}/salao`,
    reuseExistingServer: !process.env["CI"],
    timeout: 180_000,
  },
});
