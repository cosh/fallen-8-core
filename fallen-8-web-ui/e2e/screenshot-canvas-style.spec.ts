// MIT License
//
// screenshot-canvas-style.spec.ts
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
 * Docs screenshot capture (feature canvas-color-property-default): the Canvas style panel
 * with a control switched to "property", showing the property-name field seeded with a real
 * key from the graph (editable afterwards). Capture-only, gated like the other screenshot
 * specs so the normal e2e run does not depend on it:
 *
 *   F8_SCREENSHOT=1 npx playwright test e2e/screenshot-canvas-style.spec.ts
 *
 * Output: docs/images/screen-canvas-style.png.
 */

const API_KEY = process.env.F8_E2E_API_KEY ?? "e2e-key";

test.skip(process.env.F8_SCREENSHOT !== "1", "docs screenshot capture (set F8_SCREENSHOT=1)");

async function registerSecuredInstance(page: Page) {
  await page.goto("/");
  await page.getByTestId("instance-add").click();
  await page.getByTestId("instance-name").fill("e2e");
  await page.getByTestId("instance-url").fill("");
  await page.getByLabel(/api key/i).fill(API_KEY);
  await page.getByTestId("instance-save").click();
  await page.getByRole("radio", { name: "activate e2e" }).check();
  await expect(page.getByTestId("active-endpoint")).toContainText("same origin");
}

test("capture the canvas style panel with a seeded color property", async ({ page }) => {
  await page.setViewportSize({ width: 1600, height: 1000 });
  await registerSecuredInstance(page);

  // A vertex with a typed property, so the style panel has a real key ("age") to seed.
  const label = `person-shot-${Math.floor(Math.random() * 1e6)}`;
  await page.goto("/browser");
  await page.getByTestId("new-vertex-label").fill(label);
  await page.getByTestId("create-vertex").click();
  await expect(page.getByTestId("mutation-message")).toContainText(label);

  await page.locator("#max-elements").fill("5000");
  await page.getByRole("button", { name: "Load", exact: true }).click();
  await page.getByTestId("bulk-filter").fill(label);
  const row = page.locator("tr", { hasText: label }).first();
  await expect(row).toBeVisible({ timeout: 20_000 });
  const id = Number(await row.getByRole("button").first().textContent());

  await page.getByTestId("mutation-tab-property").click();
  await page.locator("#mp-element").fill(String(id));
  await page.locator("#mp-id").fill("age");
  await page.getByLabel(/^value type$/).selectOption("System.Int32");
  await page.getByTestId("mp-value").fill("42");
  await page.getByRole("button", { name: "Set property" }).click();
  await expect(page.getByTestId("mutation-message")).toContainText("age");

  // Scan it out and hand it to the canvas.
  await page.goto("/query");
  await page.getByTestId("scan-property").fill("age");
  await page.locator("#scan-operator").selectOption("Equals");
  await page.getByLabel(/^literal type$/).selectOption("System.Int32");
  await page.getByTestId("scan-literal-value").fill("42");
  await page.getByTestId("scan-run").click();
  await expect(page.getByText(/results — 1 ids/)).toBeVisible({ timeout: 20_000 });
  await page.getByTestId("send-to-canvas").click();

  await page.goto("/canvas");
  await expect(page.getByText(/1 elements/)).toBeVisible();

  // Switching a control to "property" seeds the field with the first canvas key ("age").
  await page.getByLabel("color by").first().selectOption("property");
  await expect(page.locator("#style-node-color-prop")).toHaveValue("age");
  await page.getByLabel("size by").first().selectOption("property");
  await expect(page.locator("#style-node-size-prop")).toHaveValue("age");

  await page.screenshot({ path: "../docs/images/screen-canvas-style.png" });
});
