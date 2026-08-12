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
import base from "./playwright.config";

/**
 * The embed smoke (feature studio-embeddable, phase 6): its own config because the main
 * playwright.config.ts is hard-bound to an apiApp webServer, while this suite needs no
 * database at all - it builds the library artifact, installs the host fixture against it
 * (file: dependency, so the exports map and peer resolution are what actually resolve),
 * builds the fixture with a stock vite, and serves it statically. Everything that is a
 * suite-wide convention (timeouts, workers, retries, screenshots) is INHERITED from the
 * main config, so the two suites cannot drift apart. Run via `npm run e2e:embed`; CI runs
 * it as its own job (it needs node and a browser, nothing else).
 *
 * The port is a literal, deliberately: the fixture's preview script pins
 * `--port 5199 --strictPort` (e2e-embed/host/package.json), so an env knob here could only
 * disagree with it and time out pointing at the wrong port.
 */
const EMBED_URL = "http://localhost:5199";

export default defineConfig({
  ...base,
  testDir: "./e2e-embed",
  timeout: 60_000,
  use: {
    ...base.use,
    baseURL: EMBED_URL,
  },
  webServer: {
    command: "npm run embed-smoke:serve",
    url: `${EMBED_URL}/`,
    // CI gets a guaranteed-fresh chain; locally an already-running preview on :5199 is
    // reused so iterating on the spec does not rebuild a multi-megabyte artifact per run.
    reuseExistingServer: !process.env.CI,
    // Generous: the chain builds the library artifact, npm-installs the fixture, and
    // builds the fixture before the preview server answers.
    timeout: 420_000,
  },
});
