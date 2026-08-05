// MIT License
//
// playwright.config.ts
//
// Copyright (c) 2011-2026 Henning Rauch
//
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in all
// copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
//
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
// SOFTWARE.

import { defineConfig } from "@playwright/test";

/**
 * E2E against a real apiApp serving the built SPA (scenarios in spec §9).
 *
 * Default mode: builds the SPA into ../fallen-8-core-apiApp/wwwroot and launches the
 * apiApp with volatile durability and an API key ("e2e-key"). Dynamic code execution is
 * always on, so the delegate-editor scenarios need no extra flag.
 *
 * ISOLATION INVARIANT (why the port and the two flags below are what they are): the functional
 * specs erase the `default` namespace, so a run must never be able to land on a hand-started
 * development instance. Two things enforce that instead of trusting a convention:
 *   1. the suite owns E2E_PORT, deliberately NOT the :5000 that the dev API / Studio launch
 *      configs bind, and
 *   2. `reuseExistingServer: false`, so playwright always starts its OWN apiApp (volatile
 *      durability) and can never adopt one it did not configure.
 * A port clash is therefore a hard, immediate failure rather than a silently wiped graph.
 *
 * The only way out is explicit and hand-typed: F8_UI_URL targets an already-running instance
 * and launches nothing (that is how the F8_SCREENSHOT=1 screenshot specs capture against a
 * purpose-built app). Whoever sets it owns the target's durability: point it at a throwaway.
 */
const E2E_PORT = process.env.F8_E2E_PORT ?? "5099";
const E2E_URL = `http://localhost:${E2E_PORT}`;

export default defineConfig({
  testDir: "./e2e",
  // Per-test ceiling. Kept modest so a misconfigured/unreachable backend fails fast
  // instead of every test burning a 90s timeout (a whole-suite hang reads as "slow e2e"
  // when it is really "backend not serving"). The nav/action timeouts below make the
  // first goto in each test fail in ~15s rather than sitting until the per-test ceiling.
  timeout: 45_000,
  expect: { timeout: 15_000 },
  fullyParallel: false,
  workers: 1,
  retries: 0,
  use: {
    baseURL: process.env.F8_UI_URL ?? E2E_URL,
    navigationTimeout: 15_000,
    actionTimeout: 15_000,
    screenshot: "only-on-failure",
  },
  webServer: process.env.F8_UI_URL
    ? undefined
    : {
        // `-- --urls` beats the launch profile's ASPNETCORE_URLS (:5000): command-line
        // configuration wins over environment variables, so the app really binds E2E_PORT.
        command: `npm run build:apiapp && dotnet run --project ../fallen-8-core-apiApp -- --urls ${E2E_URL}`,
        url: `${E2E_URL}/`,
        reuseExistingServer: false,
        timeout: 240_000,
        env: {
          Fallen8__Durability__Volatile: "true",
          Fallen8__Security__ApiKey: "e2e-key",
        },
      },
});
