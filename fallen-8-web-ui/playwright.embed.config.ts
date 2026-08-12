// MIT License
//
// playwright.embed.config.ts
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
 * The embed smoke (feature studio-embeddable, phase 6): its own config because the main
 * playwright.config.ts is hard-bound to an apiApp webServer, while this suite needs no
 * database at all - it builds the library artifact, installs the host fixture against it
 * (file: dependency, so the exports map and peer resolution are what actually resolve),
 * builds the fixture with a stock vite, and serves it statically. Run via `npm run
 * e2e:embed`; CI runs it in the e2e job, where playwright's chromium is already installed.
 */
const EMBED_PORT = process.env.F8_EMBED_PORT ?? "5199";
const EMBED_URL = `http://localhost:${EMBED_PORT}`;

export default defineConfig({
  testDir: "./e2e-embed",
  timeout: 60_000,
  expect: { timeout: 15_000 },
  fullyParallel: false,
  workers: 1,
  retries: 0,
  use: {
    baseURL: EMBED_URL,
    navigationTimeout: 15_000,
    actionTimeout: 15_000,
    screenshot: "only-on-failure",
  },
  webServer: {
    command: "npm run embed-smoke:serve",
    url: `${EMBED_URL}/`,
    reuseExistingServer: false,
    // Generous: the chain builds the library artifact, npm-installs the fixture, and
    // builds the fixture before the preview server answers.
    timeout: 420_000,
  },
});
