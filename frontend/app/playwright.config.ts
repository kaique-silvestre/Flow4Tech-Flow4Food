import { defineConfig } from "@playwright/test";

/**
 * Two phone sizes (docs/design/DESIGN.md): 390x844 is the design size, 360x740 the smallest supported. The app is served by `vite preview`
 * (the production build, so the service worker and the manifest are the real ones) which proxies the API paths to the real API (`API_URL`, default http://127.0.0.1:8080).
 * `PW_CHROMIUM` points at an already installed Chromium when the one Playwright wants is not available (cloud sessions); CI uses `playwright install chromium`.
 */
const executablePath = process.env["PW_CHROMIUM"];
const launchOptions = executablePath ? { executablePath, args: ["--no-sandbox"] } : { args: ["--no-sandbox"] };

export default defineConfig({
  testDir: "e2e",
  outputDir: "e2e-results/artifacts",
  fullyParallel: false,
  workers: 1,
  reporter: [["list"]],
  use: {
    baseURL: "http://127.0.0.1:4173",
    trace: "off",
    launchOptions,
    serviceWorkers: "allow",
  },
  projects: [
    { name: "phone-390x844", use: { viewport: { width: 390, height: 844 }, deviceScaleFactor: 2, isMobile: true, hasTouch: true } },
    { name: "phone-360x740", use: { viewport: { width: 360, height: 740 }, deviceScaleFactor: 2, isMobile: true, hasTouch: true } },
  ],
  webServer: {
    command: "pnpm run build && pnpm run preview",
    url: "http://127.0.0.1:4173/salao",
    reuseExistingServer: !process.env["CI"],
    timeout: 180_000,
  },
});
