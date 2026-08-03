// MIT License
//
// screenshot-canvas-find.spec.ts
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
 * Docs screenshot capture (feature canvas-find-connect): the Canvas "Find" tab with a result row
 * hovered, spotlighting its node on the canvas with the "solar eclipse" corona. Loads the
 * karate-club sample (whose members carry a searchable `faction` property), searches it, and
 * hovers the first match. Capture-only, gated like the other screenshot specs:
 *
 *   F8_SCREENSHOT=1 npx playwright test e2e/screenshot-canvas-find.spec.ts
 *
 * Output: docs/src/assets/images/screen-canvas-find.png.
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

test("capture the Find tab hover eclipse spotlight", async ({ page }) => {
  test.setTimeout(120_000);
  await page.setViewportSize({ width: 1600, height: 1000 });
  await registerSecuredInstance(page);

  await page.goto("/q/default/samples");
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
  // Let the force layout settle so the spotlight lands on a rested node.
  await page.waitForTimeout(4000);

  await page.getByTestId("canvas-tab-find").click();
  await page.getByTestId("find-term").fill("officer");
  await page.getByTestId("find-run").click();

  const firstRow = page.locator('[data-testid^="find-row-"]').first();
  await expect(firstRow).toBeVisible({ timeout: 20_000 });
  await firstRow.hover();
  // The corona is driven by a requestAnimationFrame loop; give it a couple of frames to park.
  await page.waitForTimeout(500);

  await page.screenshot({ path: "../docs/src/assets/images/screen-canvas-find.png" });
});
