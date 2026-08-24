// MIT License
//
// screenshot-wind-farm.spec.ts
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
 * Docs screenshot capture (feature knowledge-demo): the Wind Farm Fleet Integrity sample, which
 * ingests three real documents through the live semantic layer. Capture-only.
 *
 * UNLIKE the other sample screenshot specs this one needs a FULL environment, because the load
 * genuinely converts a PDF in docling, embeds through the provider and enriches in the NLP
 * sidecar. Run it against an apiApp wired to those sidecars (the compose stack's are fine):
 *
 *   npm run build:apiapp
 *   Fallen8__Ingestion__Enabled=true Fallen8__Ingestion__Docling__Endpoint=http://localhost:5001 \
 *   Fallen8__Nlp__Enabled=true Fallen8__Nlp__Endpoint=http://localhost:8100 \
 *   Fallen8__Embedding__Enabled=true Fallen8__Embedding__Backend=Ollama \
 *   Fallen8__Embedding__Ollama__Endpoint=http://localhost:11434 \
 *   dotnet run --project ../fallen-8-core-apiApp --urls http://127.0.0.1:5099
 *   F8_SCREENSHOT=1 F8_UI_URL=http://127.0.0.1:5099 npx playwright test e2e/screenshot-wind-farm.spec.ts
 *
 * Outputs:
 *   docs/src/assets/images/sample-wind-farm.png        (the gallery card, gate satisfied)
 *   docs/src/assets/images/sample-wind-farm-canvas.png (both graphs on one canvas)
 */

const API_KEY = process.env.F8_E2E_API_KEY ?? "e2e-key";

test.skip(process.env.F8_SCREENSHOT !== "1", "docs screenshot capture (set F8_SCREENSHOT=1)");

async function registerSecuredInstance(page: Page, name = "windfarm") {
  await page.goto("/");
  await page.getByTestId("instance-add").click();
  await page.getByTestId("instance-name").fill(name);
  await page.getByTestId("instance-url").fill("");
  await page.getByLabel(/api key/i).fill(API_KEY);
  await page.getByTestId("instance-save").click();
  await page.getByRole("radio", { name: `activate ${name}` }).check();
  await expect(page.getByTestId("active-endpoint")).toContainText("same origin");
}

test("capture the wind-farm sample card, its load, and both graphs on the canvas", async ({
  page,
}) => {
  // The loader tolerates up to 300s per document, so three documents can legitimately take 900s.
  // Allow for that rather than failing the capture with a Playwright timeout that hides the
  // loader's own diagnosis.
  test.setTimeout(960_000);
  await page.setViewportSize({ width: 1440, height: 1000 });
  await registerSecuredInstance(page);

  await page.goto("/q/default/samples");
  // Entered on an empty graph, so the first-run walkthrough opens itself over the gallery: its
  // scrim would dim the card shot below and swallow the load click after it.
  await closeIntroIfOpen(page);
  const card = page.getByTestId("sample-card-wind-farm");
  await expect(card).toBeVisible({ timeout: 30_000 });
  await card.scrollIntoViewIfNeeded();

  // The environment must actually be able to run this sample; a blocking gate would both fail
  // the load below and make a misleading screenshot.
  for (const blocked of ["gate-ingestion-off", "gate-provider-off", "gate-docling-unreachable"]) {
    await expect(card.getByTestId(blocked)).toHaveCount(0);
  }
  await card.screenshot({ path: "../docs/src/assets/images/sample-wind-farm.png" });

  await page.getByTestId("load-sample-wind-farm").click();
  const typed = page.getByTestId("confirm-typed");
  try {
    await typed.waitFor({ state: "visible", timeout: 2500 });
    await typed.fill("windfarm");
    await page.getByTestId("confirm-action").click();
  } catch {
    // empty graph: it loaded directly, no confirm
  }
  // The message reports the ingest too, which is the part worth asserting: a load that imported
  // the graph but ingested nothing would still say "Loaded".
  const message = page.getByTestId("sample-message");
  await expect(message).toContainText("Loaded", { timeout: 900_000 });
  await expect(message).toContainText("document(s) ingested");

  await page.goto("/canvas");
  const canvas = page.getByTestId("graph-canvas");
  await expect(canvas).toBeVisible();
  await page.waitForTimeout(4000); // let the layout settle over both graphs
  await canvas.screenshot({ path: "../docs/src/assets/images/sample-wind-farm-canvas.png" });
});
