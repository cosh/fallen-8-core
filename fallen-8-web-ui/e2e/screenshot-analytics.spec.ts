// MIT License
//
// screenshot-analytics.spec.ts
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
 * Docs screenshot capture: the Analytics screen before a run (algorithm picker + options).
 * Capture-only. Imports the Karate Club sample so the graph is ready to analyze.
 *
 *   F8_SCREENSHOT=1 F8_UI_URL=http://127.0.0.1:<port> npx playwright test e2e/screenshot-analytics.spec.ts
 *
 * Output: docs/src/assets/images/screen-analytics-before.png.
 */

const API_KEY = process.env.F8_E2E_API_KEY ?? "e2e-key";
const AUTH = { Authorization: `Bearer ${API_KEY}` };
const NDJSON = { ...AUTH, "Content-Type": "application/x-ndjson" };

test.skip(process.env.F8_SCREENSHOT !== "1", "docs screenshot capture (set F8_SCREENSHOT=1)");

test("capture the Analytics screen before a run", async ({ page, request }) => {
  await page.setViewportSize({ width: 1440, height: 900 });

  await request.head("/tabularasa/all", { headers: AUTH });
  const jsonl = readFileSync(path.resolve(process.cwd(), "../samples/karate-club.jsonl"), "utf8");
  expect((await request.post("/bulk/import", { headers: NDJSON, data: jsonl })).ok()).toBeTruthy();

  await page.goto("/");
  await page.getByTestId("instance-add").click();
  await page.getByTestId("instance-name").fill("local");
  await page.getByTestId("instance-url").fill("");
  await page.getByLabel(/api key/i).fill(API_KEY);
  await page.getByTestId("instance-save").click();
  await page.getByRole("radio", { name: "activate local" }).last().check();
  await expect(page.getByTestId("active-endpoint")).toContainText("same origin");
  await expect(page.getByTestId("health-chip")).toContainText(/online/i, { timeout: 20_000 });

  await page.goto("/q/default/analytics");
  await expect(page.getByTestId("algo-name")).toBeVisible({ timeout: 20_000 });

  await page.screenshot({ path: "../docs/src/assets/images/screen-analytics-before.png" });
});
