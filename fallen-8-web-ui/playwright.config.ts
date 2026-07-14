import { defineConfig } from "@playwright/test";

/**
 * E2E against a real apiApp serving the built SPA (scenarios in spec §9).
 *
 * Default mode: builds the SPA into ../fallen-8-core-apiApp/wwwroot and launches the
 * apiApp with volatile durability, an API key ("e2e-key"), and dynamic code execution
 * enabled - so the delegate-editor scenarios can run. Set F8_UI_URL to target an
 * already-running instance instead.
 */
export default defineConfig({
  testDir: "./e2e",
  timeout: 90_000,
  fullyParallel: false,
  workers: 1,
  retries: 0,
  use: {
    baseURL: process.env.F8_UI_URL ?? "http://localhost:5000",
    screenshot: "only-on-failure",
  },
  webServer: process.env.F8_UI_URL
    ? undefined
    : {
        command: "npm run build:apiapp && dotnet run --project ../fallen-8-core-apiApp",
        url: "http://localhost:5000/",
        reuseExistingServer: true,
        timeout: 240_000,
        env: {
          Fallen8__Durability__Volatile: "true",
          Fallen8__Security__ApiKey: "e2e-key",
          Fallen8__Security__EnableDynamicCodeExecution: "true",
        },
      },
});
