// MIT License
//
// screenshot-benchmark.spec.ts
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

import { expect, test } from "@playwright/test";
import { readFileSync } from "node:fs";
import path from "node:path";

/**
 * Docs screenshot capture (feature schema-agnostic-benchmark): the Benchmark tab pointed at a
 * NON-generated graph — the Karate Club sample (34 vertices / 78 edges), whose edges are labelled
 * "knows", never the generator's "A". This proves the point of the feature: the benchmark now
 * measures whatever graph is loaded. Capture-only; imports the sample via REST, activates the
 * seeded same-origin instance, then screenshots /benchmarks without running (a run would add
 * machine-dependent TPS numbers and a history table).
 *
 *   F8_SCREENSHOT=1 npx playwright test e2e/screenshot-benchmark.spec.ts
 *
 * Output: docs/images/screen-benchmark.png. Volatile durability is fine (no save-games needed);
 * the default webServer config (see playwright.config.ts) provides exactly that.
 */

const API_KEY = process.env.F8_E2E_API_KEY ?? "e2e-key";
const AUTH = { Authorization: `Bearer ${API_KEY}` };
const NDJSON = { ...AUTH, "Content-Type": "application/x-ndjson" };

test.skip(process.env.F8_SCREENSHOT !== "1", "docs screenshot capture (set F8_SCREENSHOT=1)");

test("capture the Benchmark tab on a loaded (non-generated) graph", async ({ page, request }) => {
  await page.setViewportSize({ width: 1600, height: 1000 });

  // Idempotent reset so the capture is re-runnable, then import the Karate Club sample into the
  // default namespace. Its edges are labelled "knows" — the benchmark following them (not just the
  // generator's "A") is the whole feature.
  await request.head("/tabularasa/all", { headers: AUTH });
  const jsonl = readFileSync(
    path.resolve(process.cwd(), "../samples/karate-club.jsonl"),
    "utf8",
  );
  expect((await request.post("/bulk/import", { headers: NDJSON, data: jsonl })).ok()).toBeTruthy();

  // The auto-seeded same-origin "local" instance has no persistent key (feature standalone-ui
  // never persists it, so editing its key would not survive a reload). Register a keyed
  // same-origin instance and activate it (its /status then fetches with the key -> online).
  await page.goto("/");
  await page.getByTestId("instance-add").click();
  await page.getByTestId("instance-name").fill("local");
  await page.getByTestId("instance-url").fill("");
  await page.getByLabel(/api key/i).fill(API_KEY);
  await page.getByTestId("instance-save").click();
  await page.getByRole("radio", { name: "activate local" }).last().check();
  await expect(page.getByTestId("active-endpoint")).toContainText("same origin");
  await expect(page.getByTestId("health-chip")).toContainText(/online/i, { timeout: 20_000 });

  await page.goto("/benchmarks");
  // Both panels rendered and the header reflects the loaded sample (34 vertices) — proving it is a
  // real loaded graph, not a generated one.
  await expect(page.getByTestId("run-benchmark")).toBeVisible();
  await expect(page.getByText(/34 vertices/)).toBeVisible({ timeout: 20_000 });

  await page.screenshot({ path: "../docs/src/assets/images/screen-benchmark.png" });
});
