// MIT License
//
// screenshot-canvas-connect.spec.ts
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

import { closeIntroIfOpen } from "./firstRun";

/**
 * Docs screenshot capture (feature canvas-find-connect): the Canvas "Connect" tab finding
 * shortest paths between the vertices on the canvas. Loads the karate-club sample (a small,
 * fully-connected graph), picks a handful of vertices, and runs the pairwise search so the shot
 * shows the pick list, the pair budget, and the found connections with their add/remove actions.
 * Capture-only, gated like the other screenshot specs:
 *
 *   F8_SCREENSHOT=1 npx playwright test e2e/screenshot-canvas-connect.spec.ts
 *
 * Output: docs/src/assets/images/screen-canvas-connect.png.
 */

const API_KEY = process.env.F8_E2E_API_KEY ?? "e2e-key";
const INSTANCE_NAME = "docs";

test.skip(process.env.F8_SCREENSHOT !== "1", "docs screenshot capture (set F8_SCREENSHOT=1)");

async function registerSecuredInstance(page: Page) {
  await page.goto("/");
  await page.getByTestId("instance-add").click();
  await page.getByTestId("instance-name").fill(INSTANCE_NAME);
  await page.getByTestId("instance-url").fill("");
  await page.getByLabel(/api key/i).fill(API_KEY);
  await page.getByTestId("instance-save").click();
  await page.getByRole("radio", { name: `activate ${INSTANCE_NAME}` }).check();
  await expect(page.getByTestId("active-endpoint")).toContainText("same origin");
}

test("capture the canvas Connect tab finding paths between picked vertices", async ({ page }) => {
  test.setTimeout(120_000);
  await page.setViewportSize({ width: 1600, height: 1000 });
  await registerSecuredInstance(page);

  // Karate club: a small, single-component graph, so any picked vertices have paths.
  await page.goto("/q/default/samples");
  // The graph is empty at this point, so the first-run walkthrough opens itself over this
  // screen and is modal: the sample card below is unclickable until it is closed.
  await closeIntroIfOpen(page);
  await expect(page.getByTestId("sample-card-karate-club")).toBeVisible({ timeout: 30_000 });
  await page.getByTestId("load-sample-karate-club").click();
  const typed = page.getByTestId("confirm-typed");
  try {
    await typed.waitFor({ state: "visible", timeout: 2500 });
    await typed.fill(INSTANCE_NAME);
    await page.getByTestId("confirm-action").click();
  } catch {
    // fresh graph: loaded directly, no wipe confirm
  }
  await expect(page.getByTestId("sample-message")).toContainText("Loaded", { timeout: 60_000 });

  await page.goto("/canvas");
  await expect(page.getByTestId("graph-canvas")).toBeVisible();

  // Connect tab: all 34 vertices are over the pair budget, so narrow with pick mode.
  await page.getByTestId("canvas-tab-connect").click();
  await expect(page.getByTestId("connect-over-cap")).toBeVisible();
  await page.getByTestId("connect-scope-pick").click();

  const checks = page.locator('[data-testid^="connect-pick-"] input[type="checkbox"]');
  await expect(checks.first()).toBeVisible();
  for (let i = 0; i < 4; i++) await checks.nth(i).check();
  await expect(page.getByTestId("connect-pair-count")).toContainText("4 vertices → 6 pairs");

  await page.getByTestId("connect-run").click();
  await expect(page.getByTestId("connect-summary")).toBeVisible({ timeout: 30_000 });
  await expect(page.getByTestId("connect-summary")).toContainText("connections found");

  await page.screenshot({ path: "../docs/src/assets/images/screen-canvas-connect.png" });
});
