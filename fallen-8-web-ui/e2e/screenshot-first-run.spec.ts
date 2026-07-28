// MIT License
//
// screenshot-first-run.spec.ts
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
 * Docs screenshot capture (feature studio-first-run): the first-run show settled on its composed
 * final state + handoff. Capture-only - skipped in the normal e2e run (which shares one apiApp
 * and would leave the default namespace erased). Run it deliberately:
 *
 *   F8_SCREENSHOT=1 F8_UI_URL=http://127.0.0.1:5000 npx playwright test e2e/screenshot-first-run.spec.ts
 *
 * Output: docs/images/screen-first-run.png.
 */

const API_KEY = process.env.F8_E2E_API_KEY ?? "e2e-key";

test.skip(process.env.F8_SCREENSHOT !== "1", "docs screenshot capture (set F8_SCREENSHOT=1)");

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

test("capture the first-run show", async ({ page }) => {
  await page.setViewportSize({ width: 1440, height: 1000 });
  await registerSecuredInstance(page);

  // Guarantee the empty state so the show auto-appears.
  await page.goto("/save-games");
  await page.getByTestId("tabularasa").click();
  await page.getByTestId("confirm-typed").fill("default");
  await page.getByTestId("confirm-action").click();
  await expect(page.getByTestId("admin-message")).toContainText("erased", { timeout: 20_000 });

  await page.goto("/dashboard");
  await expect(page.getByTestId("first-run-show")).toBeVisible();
  // Let the staggered bloom finish (~1.1s) so every emoji vertex is drawn, then jump to the Path
  // beat (the blast-radius story) so the capture shows the highlighted directed route + caption.
  await page.waitForTimeout(1600);
  await page.getByTestId("first-run-dot-1").click();
  await expect(page.getByTestId("first-run-caption")).toContainText("blast radius");
  await page.waitForTimeout(700);

  await page.screenshot({ path: "../docs/src/assets/images/screen-first-run.png" });
});
