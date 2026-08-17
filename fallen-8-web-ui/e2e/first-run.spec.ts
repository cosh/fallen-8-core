// MIT License
//
// first-run.spec.ts
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

import { expect, test, type Page } from "@playwright/test";

/**
 * First-run show end-to-end (feature studio-first-run). The e2e apiApp requires an API key and
 * starts empty, so each test registers a same-origin keyed instance through the real Connect
 * screen and erases the default namespace to guarantee the empty state it exercises.
 *
 * The load-bearing guarantee: the show creates nothing. Every test that plays the show asserts
 * that ZERO non-GET requests reach the instance while it runs - for both the auto-show and the
 * manual replay. Only the explicit handoff "Load the sample graph" button writes, on click.
 */

const API_KEY = process.env.F8_E2E_API_KEY ?? "e2e-key";
const ORIGIN = "http://localhost:5000";

async function registerSecuredInstance(page: Page, name = "firstrun") {
  await page.goto("/");
  await page.getByTestId("instance-add").click();
  await page.getByTestId("instance-name").fill(name);
  await page.getByTestId("instance-url").fill("");
  await page.getByLabel(/api key/i).fill(API_KEY);
  await page.getByTestId("instance-save").click();
  await page.getByRole("radio", { name: `activate ${name}` }).check();
  await expect(page.getByTestId("active-endpoint")).toContainText("same origin");
}

/** Erase the default namespace via the Save games administration flow (typed NAMESPACE name). */
async function eraseDefault(page: Page) {
  await page.goto("/save-games");
  await page.getByTestId("tabularasa").click();
  await page.getByTestId("confirm-typed").fill("default");
  await page.getByTestId("confirm-action").click();
  await expect(page.getByTestId("admin-message")).toContainText("erased", { timeout: 20_000 });
}

/** Records every non-GET request to the instance from now on (writes the show must never make). */
function trackWrites(page: Page): string[] {
  const writes: string[] = [];
  page.on("request", (req) => {
    if (req.method() !== "GET" && req.url().startsWith(ORIGIN)) {
      writes.push(`${req.method()} ${req.url().slice(ORIGIN.length)}`);
    }
  });
  return writes;
}

test("auto-shows on an empty graph and creates nothing while it plays", async ({ page }) => {
  await registerSecuredInstance(page);
  await eraseDefault(page);

  const writes = trackWrites(page); // only writes AFTER the erase count against the show
  await page.goto("/dashboard");

  await expect(page.getByTestId("first-run-show")).toBeVisible();
  await expect(page.getByTestId("first-run-caption")).toContainText("A graph is");

  // Let a couple of beats play, then settle on the handoff deterministically.
  await page.waitForTimeout(2000);
  await page.getByTestId("first-run-skip").click();
  await expect(page.getByTestId("first-run-handoff")).toBeVisible();

  expect(writes, `unexpected writes during the show: ${writes.join(", ")}`).toEqual([]);
});

test("the handoff jumps to the Sample gallery and never touches the unit-test graph", async ({
  page,
}) => {
  await registerSecuredInstance(page);
  await eraseDefault(page);

  // Fail loudly if the UI ever calls the unit-test endpoint (it must not; see CLAUDE.md).
  const unittestCalls: string[] = [];
  page.on("request", (req) => {
    if (req.url().includes("/unittest")) unittestCalls.push(`${req.method()} ${req.url()}`);
  });
  const writes = trackWrites(page);

  await page.goto("/dashboard");
  await expect(page.getByTestId("first-run-show")).toBeVisible();
  // Jump to the handoff rather than waiting out the ~50s autoplay.
  await page.getByTestId("first-run-skip").click();
  await page.getByTestId("first-run-browse-samples").click();

  // It navigates to the curated Sample gallery, writing nothing and never hitting /unittest.
  await expect(page).toHaveURL(/\/q\/default\/samples/);
  await expect(page.getByTestId("nav-samples")).toHaveClass(/text-accent/);
  expect(unittestCalls, `the UI hit the unit-test endpoint: ${unittestCalls.join(", ")}`).toEqual([]);
  expect(writes, `unexpected writes: ${writes.join(", ")}`).toEqual([]);
});

test("replays on demand as an overlay, on the mock, writing nothing, and restores the screen", async ({
  page,
}) => {
  await registerSecuredInstance(page);
  // Populate via the Benchmark generator so we are on an ordinary populated screen (proves replay
  // works when NOT empty). The unit-test graph is deliberately never used in the UI.
  await page.goto("/q/default/benchmarks");
  await page.getByTestId("generate-sample").click();
  await expect(page.getByTestId("generate-result")).toBeVisible({ timeout: 30_000 });

  await page.goto("/browser");
  await expect(page).toHaveURL(/\/q\/default\/browser/);

  const writes = trackWrites(page);
  await page.getByTestId("nav-replay-intro").click();

  const overlay = page.getByTestId("first-run-overlay");
  await expect(overlay).toBeVisible();
  await expect(overlay.getByTestId("first-run-show")).toBeVisible();
  // It uses the internal mock, never the user's real data (a mock-only node label). Exact match:
  // Playwright getByText is substring+case-insensitive, and "threat actor" also appears in a caption.
  await expect(overlay.getByText("Threat Actor", { exact: true })).toBeVisible();

  await page.waitForTimeout(1500);
  await page.getByTestId("first-run-overlay-close").click();
  await expect(overlay).not.toBeVisible();

  // Returned exactly where we were, and wrote nothing while the overlay played.
  await expect(page).toHaveURL(/\/q\/default\/browser/);
  expect(writes, `unexpected writes during manual replay: ${writes.join(", ")}`).toEqual([]);

  // The manual path never touched the dismissed flag (the graph is populated; nothing dismissed).
  const persisted = await page.evaluate(() => window.localStorage.getItem("f8.first-run"));
  const dismissed = persisted ? (JSON.parse(persisted).state?.dismissed ?? {}) : {};
  expect(Object.keys(dismissed)).toEqual([]);
});
