// MIT License
//
// screenshot-sample-canvases.spec.ts
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
 * Docs screenshot capture: the full-page canvas shots of the file-based sample graphs
 * used by docs/src/content/docs/samples.md. Each sample is loaded from the gallery
 * (its baked-in style applies automatically), rendered on the Canvas screen, and the
 * whole Studio window captured. Capture-only, gated like the other screenshot specs:
 *
 *   F8_SCREENSHOT=1 npx playwright test e2e/screenshot-sample-canvases.spec.ts
 *
 * Needs the sample manifest reachable (same-origin /samples, present in a build:apiapp
 * wwwroot). Outputs docs/src/assets/images/sample-<id>.png for each entry below.
 */

const API_KEY = process.env.F8_E2E_API_KEY ?? "e2e-key";

test.skip(process.env.F8_SCREENSHOT !== "1", "docs screenshot capture (set F8_SCREENSHOT=1)");

/** settleMs: 2D force layout time before the shot; big graphs need longer to untangle. */
const SHOTS: { id: string; settleMs: number }[] = [
  { id: "karate-club", settleMs: 5_000 },
  { id: "attack-surface", settleMs: 6_000 },
  { id: "movie-night", settleMs: 9_000 },
  { id: "air-routes", settleMs: 12_000 },
  { id: "fallen8-deps", settleMs: 9_000 },
];

const INSTANCE_NAME = "docs";

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

test("capture every file sample on the canvas", async ({ page }) => {
  // Five sequential load+settle rounds exceed the default per-test budget.
  test.setTimeout(300_000);
  await page.setViewportSize({ width: 1600, height: 1000 });
  await registerSecuredInstance(page);

  for (const { id, settleMs } of SHOTS) {
    await page.goto("/q/default/samples");
    // The first pass enters on an empty graph, so the walkthrough opens over the gallery and
    // its scrim would swallow the load click.
    await closeIntroIfOpen(page);
    const card = page.getByTestId(`sample-card-${id}`);
    await expect(card).toBeVisible({ timeout: 30_000 });
    await page.getByTestId(`load-sample-${id}`).click();

    // A non-empty graph is wiped first behind a typed confirm (empty graph: none).
    const typed = page.getByTestId("confirm-typed");
    try {
      await typed.waitFor({ state: "visible", timeout: 2500 });
      await typed.fill(INSTANCE_NAME);
      await page.getByTestId("confirm-action").click();
    } catch {
      // empty graph: it loaded directly, no confirm
    }
    await expect(page.getByTestId("sample-message")).toContainText("Loaded", {
      timeout: 60_000,
    });

    await page.goto("/canvas");
    await expect(page.getByTestId("graph-canvas")).toBeVisible();
    await page.waitForTimeout(settleMs);
    await page.screenshot({ path: `../docs/src/assets/images/sample-${id}.png` });
  }
});
