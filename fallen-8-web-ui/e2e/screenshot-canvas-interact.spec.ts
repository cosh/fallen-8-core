// MIT License
//
// screenshot-canvas-interact.spec.ts
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
 * Docs screenshot capture (feature canvas-interact): the Canvas "Interact" tab with a filter
 * narrowing the canvas and both bulk verbs armed. Loads the karate-club sample (small, fully
 * connected), then previews a DATABASE-degree filter, so the shot shows the evaluated match list
 * and the two action buttons carrying the count they would act on - the part that distinguishes
 * this tab from a styling panel.
 * Capture-only, gated like the other screenshot specs:
 *
 *   F8_SCREENSHOT=1 npx playwright test e2e/screenshot-canvas-interact.spec.ts
 *
 * Output: docs/src/assets/images/screen-canvas-interact.png.
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

test("capture the canvas Interact tab with an evaluated match set", async ({ page }) => {
  test.setTimeout(120_000);
  await page.setViewportSize({ width: 1600, height: 1000 });
  await registerSecuredInstance(page);

  await page.goto("/q/default/samples");
  // The graph is empty here, so the first-run walkthrough opens itself, modal, over this screen.
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

  await page.getByTestId("canvas-tab-interact").click();
  await expect(page.getByTestId("interact-panel")).toBeVisible();

  // The hubs of the karate club: a true-degree filter, which is the one that needs Preview.
  await page.getByTestId("interact-degree-value").fill("8");
  await expect(page.getByTestId("interact-count")).toContainText("evaluate to match");

  await page.getByTestId("interact-preview").click();
  await expect(page.getByTestId("interact-count")).toContainText("vertices match", {
    timeout: 60_000,
  });
  // A shot of an empty match set would show none of what this tab does.
  await expect(page.locator('[data-testid^="interact-match-"]').first()).toBeVisible();

  await page.screenshot({ path: "../docs/src/assets/images/screen-canvas-interact.png" });
});
