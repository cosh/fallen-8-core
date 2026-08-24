// MIT License
//
// screenshot-events.spec.ts
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
 * Docs screenshot capture: the Events panel (feature studio-event-feed) over a LIVE
 * change feed - the graph is mutated via REST while Studio's stream is up, so the rows
 * are genuinely delivered events, not seeded state.
 *
 *   F8_SCREENSHOT=1 F8_UI_URL=http://127.0.0.1:<port> npx playwright test e2e/screenshot-events.spec.ts
 *
 * Output: docs/src/assets/images/screen-events.png.
 */

const API_KEY = process.env.F8_E2E_API_KEY ?? "e2e-key";
const AUTH = { Authorization: `Bearer ${API_KEY}` };

test.skip(process.env.F8_SCREENSHOT !== "1", "docs screenshot capture (set F8_SCREENSHOT=1)");

test("capture the Events panel with live change-feed rows", async ({ page, request }) => {
  await page.setViewportSize({ width: 1440, height: 900 });

  // Idempotent reset; the mutations happen AFTER the stream is up (see below).
  await request.head("/tabularasa/all", { headers: AUTH });

  // Activate the auto-seeded same-origin "local" instance with the e2e key (same
  // handshake as screenshot-savegames), then reload so /status refetches with the key.
  await page.goto("/");
  await page.getByTestId("instance-add").click();
  await page.getByTestId("instance-name").fill("local");
  await page.getByTestId("instance-url").fill("");
  await page.getByLabel(/api key/i).fill(API_KEY);
  await page.getByTestId("instance-save").click();
  await page.getByRole("radio", { name: "activate local" }).last().check();
  await expect(page.getByTestId("active-endpoint")).toContainText("same origin");
  await expect(page.getByTestId("health-chip")).toContainText(/online/i, { timeout: 20_000 });

  await page.goto("/q/default/browser");
  // The stream must be up BEFORE mutating, so the events arrive live and the bell counts.
  await expect(page.getByTestId("live-chip")).toHaveText("live", { timeout: 20_000 });

  // A small generated graph: every vertex/edge creation lands in the feed.
  expect(
    // Namespace-scoped: /generate has no bare alias to "default" (feature graph-namespaces).
    (await request.get("/ns/default/generate?nodeCount=14&edgeCount=2", { headers: AUTH })).ok(),
  ).toBeTruthy();

  // The bell signals without being clicked; then the panel shows the rows.
  await expect(page.getByTestId("event-feed-badge")).toBeVisible({ timeout: 20_000 });
  await page.getByTestId("event-feed-bell").click();
  await expect(page.getByTestId("event-feed-panel")).toBeVisible();
  await expect(page.getByTestId("event-feed-list").locator("li").first()).toBeVisible();

  await page.screenshot({ path: "../docs/src/assets/images/screen-events.png" });
});
