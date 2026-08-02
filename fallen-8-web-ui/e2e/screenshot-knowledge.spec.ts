// MIT License
//
// screenshot-knowledge.spec.ts
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
 * Docs screenshot capture: the Knowledge screen (feature semantic-layer) - the State panel
 * (index binding), the drag-and-drop ingest, the document table with the chunk budget, the
 * Entities view, and a fused search with hits. Capture-only. The semantic layer creates no
 * index implicitly, so this binds it first (POST /document/binding/ensure), then ingests two
 * markdown documents through the REAL text endpoint (needs Fallen8:Ingestion:Enabled=true, the
 * compose default). Ingestion is async now, so it waits for the documents to reach `indexed`.
 * The embedding provider may be on or off - the screen states either mode honestly.
 *
 *   F8_SCREENSHOT=1 F8_UI_URL=http://127.0.0.1:<port> npx playwright test e2e/screenshot-knowledge.spec.ts
 *
 * Output: docs/src/assets/images/screen-knowledge.png.
 */

const API_KEY = process.env.F8_E2E_API_KEY ?? "e2e-key";
const AUTH = { Authorization: `Bearer ${API_KEY}` };
const JSON_HEADERS = { ...AUTH, "Content-Type": "application/json" };

test.skip(process.env.F8_SCREENSHOT !== "1", "docs screenshot capture (set F8_SCREENSHOT=1)");

test("capture the Knowledge screen", async ({ page, request }) => {
  await page.setViewportSize({ width: 1440, height: 1400 });

  await request.head("/tabularasa/all", { headers: AUTH });

  // The semantic layer creates no index implicitly (FR-7): bind it before ingesting.
  expect((await request.post("/document/binding/ensure", { headers: AUTH })).ok()).toBeTruthy();

  const embed = (await request.get("/status")).ok()
    ? ((await (await request.get("/status")).json()).embedding?.enabled ?? false)
    : false;

  const ingest = (name: string, text: string) =>
    request.post("/document/text", {
      headers: JSON_HEADERS,
      data: { name, text, embed },
    });

  expect(
    (
      await ingest(
        "edge-servers.md",
        "# Edge servers\n\nThe EDGE_TLS_01 box terminates tls for the shop; certificates rotate monthly.\n\n## Racks\n\nRack three, slot one, behind the load balancer pair.",
      )
    ).ok(),
  ).toBeTruthy();
  expect(
    (
      await ingest(
        "billing-notes.md",
        "# Billing\n\nInvoices batch nightly; the RETRY_BUDGET_MS knob bounds the payment gateway retries.",
      )
    ).ok(),
  ).toBeTruthy();

  // Ingestion runs off-thread; wait for both documents to finish indexing before the shot.
  await expect
    .poll(
      async () => {
        const response = await request.get("/document", { headers: AUTH });
        if (!response.ok()) return false;
        const documents = (await response.json()).documents ?? [];
        return documents.length >= 2 && documents.every((d: { status: string }) => d.status === "indexed");
      },
      { timeout: 30_000, intervals: [500] },
    )
    .toBeTruthy();

  await page.goto("/");
  await page.getByRole("button", { name: "Edit" }).first().click();
  await page.getByLabel(/api key/i).fill(API_KEY);
  await page.getByTestId("instance-save").click();
  await page.getByRole("radio", { name: "activate local" }).check();
  await expect(page.getByTestId("active-endpoint")).toContainText("same origin");
  await page.reload();
  await expect(page.getByTestId("health-chip")).toContainText(/online/i, { timeout: 20_000 });

  await page.goto("/q/default/knowledge");
  await expect(page.getByTestId("state-panel")).toBeVisible({ timeout: 20_000 });
  await expect(page.getByTestId("chunk-budget")).toBeVisible({ timeout: 20_000 });
  await expect(page.getByText("edge-servers.md")).toBeVisible();

  // A fused search with hits makes the read path the subject too.
  await page.getByTestId("search-query").fill("who terminates tls for the shop");
  await page.getByTestId("search-run").click();
  await expect(page.getByTestId("search-results")).toBeVisible({ timeout: 20_000 });

  await page.screenshot({ path: "../docs/src/assets/images/screen-knowledge.png", fullPage: true });
});
