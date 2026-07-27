// MIT License
//
// screenshot-cyber-sample.spec.ts
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
 * Docs screenshot capture (feature studio-first-run / sample-graphs): the "Asymmetric Cyber
 * Warfare" sample in the gallery and on the canvas (2D force + a fun 3D layout). Capture-only.
 *
 * It needs the sample manifest reachable, so run it against an app built with
 * VITE_F8_SAMPLES_BASE pointing at a mirror of the repo samples/ (the orchestration script wires
 * this up). Outputs:
 *   docs/images/screen-samples.png            (gallery, cyber-warfare card first)
 *   docs/images/sample-cyber-warfare.png      (2D force canvas)
 *   docs/images/sample-cyber-warfare-3d.png   (3D dag-radial canvas)
 */

const API_KEY = process.env.F8_E2E_API_KEY ?? "e2e-key";

test.skip(process.env.F8_SCREENSHOT !== "1", "docs screenshot capture (set F8_SCREENSHOT=1)");

async function registerSecuredInstance(page: Page, name = "cyber") {
  await page.goto("/");
  await page.getByTestId("instance-add").click();
  await page.getByTestId("instance-name").fill(name);
  await page.getByTestId("instance-url").fill("");
  await page.getByLabel(/api key/i).fill(API_KEY);
  await page.getByTestId("instance-save").click();
  await page.getByRole("radio", { name: `activate ${name}` }).check();
  await expect(page.getByTestId("active-endpoint")).toContainText("same origin");
}

test("capture the cyber-warfare sample in the gallery and on the canvas", async ({ page }) => {
  await page.setViewportSize({ width: 1440, height: 1000 });
  await registerSecuredInstance(page);

  // Gallery: the new sample is the first card. Samples is namespace-scoped (no /samples alias).
  await page.goto("/q/default/samples");
  const card = page.getByTestId("sample-card-cyber-warfare");
  await expect(card).toBeVisible({ timeout: 30_000 });
  await page.screenshot({ path: "../docs/images/screen-samples.png" });

  // Load it, then render it on the canvas. If the graph is non-empty, a typed-confirm appears
  // (loading runs Tabula rasa first); arm it with the instance name.
  await page.getByTestId("load-sample-cyber-warfare").click();
  const typed = page.getByTestId("confirm-typed");
  try {
    await typed.waitFor({ state: "visible", timeout: 2500 });
    await typed.fill("cyber");
    await page.getByTestId("confirm-action").click();
  } catch {
    // empty graph: it loaded directly, no confirm
  }
  await expect(page.getByTestId("sample-message")).toContainText("Loaded", { timeout: 30_000 });

  await page.goto("/canvas");
  const canvas = page.getByTestId("graph-canvas");
  await expect(canvas).toBeVisible();

  // The 3D renderer (3d-force-graph) auto-frames the camera, so a small graph fills the frame
  // nicely; the 2D canvas has no auto-fit and scatters a 6-node graph off-screen. Capture two 3D
  // layouts: the structured radial DAG (primary) and force. Emoji nodes + directed arrows come
  // from the sample's own style.
  await page.getByTestId("style-renderer").selectOption("3d");
  await expect(canvas).toBeVisible();

  await page.getByTestId("style-layout").selectOption("dag-radial");
  await page.waitForTimeout(3500); // let the 3D layout settle and the camera frame it
  await canvas.screenshot({ path: "../docs/images/sample-cyber-warfare.png" });

  await page.getByTestId("style-layout").selectOption("force");
  await page.waitForTimeout(3500);
  await canvas.screenshot({ path: "../docs/images/sample-cyber-warfare-3d.png" });
});
