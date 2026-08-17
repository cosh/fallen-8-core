// MIT License
//
// screenshot-dashboard.spec.ts
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

/**
 * Docs screenshot capture: the Dashboard screen showing live vertex/edge/memory stats.
 * Capture-only. Generates a 200-vertex / 1,000-edge graph via REST so the counts are non-trivial.
 *
 *   F8_SCREENSHOT=1 F8_UI_URL=http://127.0.0.1:<port> npx playwright test e2e/screenshot-dashboard.spec.ts
 *
 * Output: docs/src/assets/images/screen-dashboard.png.
 */

const API_KEY = process.env.F8_E2E_API_KEY ?? "e2e-key";
const AUTH = { Authorization: `Bearer ${API_KEY}` };

test.skip(process.env.F8_SCREENSHOT !== "1", "docs screenshot capture (set F8_SCREENSHOT=1)");

test("capture the Dashboard screen with a loaded graph", async ({ page, request }) => {
  await page.setViewportSize({ width: 1440, height: 900 });

  // Idempotent reset, then a 200-vertex graph with 5 out-edges each (1,000 edges).
  await request.head("/tabularasa/all", { headers: AUTH });
  expect(
    // Namespace-scoped: /generate has no bare alias to "default" (feature graph-namespaces).
    (await request.get("/ns/default/generate?nodeCount=200&edgeCount=5", { headers: AUTH })).ok(),
  ).toBeTruthy();

  // Activate the auto-seeded same-origin "local" instance with the e2e key (same handshake as
  // screenshot-savegames), then reload so /status refetches with the key and the nav opens.
  await page.goto("/");
  await page.getByTestId("instance-add").click();
  await page.getByTestId("instance-name").fill("local");
  await page.getByTestId("instance-url").fill("");
  await page.getByLabel(/api key/i).fill(API_KEY);
  await page.getByTestId("instance-save").click();
  await page.getByRole("radio", { name: "activate local" }).last().check();
  await expect(page.getByTestId("active-endpoint")).toContainText("same origin");
  await expect(page.getByTestId("health-chip")).toContainText(/online/i, { timeout: 20_000 });

  await page.goto("/q/default/dashboard");
  await expect(page.getByRole("heading", { name: /dashboard/i })).toBeVisible({ timeout: 20_000 });
  await expect(page.getByText(/used memory/i)).toBeVisible({ timeout: 20_000 });

  await page.screenshot({ path: "../docs/src/assets/images/screen-dashboard.png" });
});
