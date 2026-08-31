// MIT License
//
// screenshot-semantic-search.spec.ts
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
 * Docs screenshot capture for the semantic-search frame on the Samples page. Output:
 *   docs/src/assets/images/query-semantic-search.png
 *
 * NEEDS AN EMBEDDING PROVIDER. The frame is the "semantic search" query mode, where the query
 * sentence is embedded server-side before the kNN runs, so it cannot be produced without one: with
 * the provider off Studio disables the query text and the run button stays dead. Wire the
 * Fallen8:Embedding section on the capture app (this is the compose default), e.g.
 *
 *   Fallen8__Embedding__Enabled=true  Fallen8__Embedding__Backend=Ollama
 *   Fallen8__Embedding__ModelName=bge-m3  Fallen8__Embedding__Dimension=1024
 *   Fallen8__Embedding__IntendedMetric=Cosine
 *   Fallen8__Embedding__Ollama__Endpoint=http://127.0.0.1:11434
 *   Fallen8__Embedding__Ollama__Model=bge-m3:latest
 *
 * With the provider absent this SKIPS instead of shooting. A skip leaves the good image alone, where
 * a degraded shot would overwrite it: that is how screen-connect-observability.png was lost five
 * times over before its own guard existed.
 *
 *   F8_SCREENSHOT=1 npm run e2e -- screenshot-semantic-search
 */

const API_KEY = process.env.F8_E2E_API_KEY ?? "e2e-key";
const AUTH = { Authorization: `Bearer ${API_KEY}` };
const INSTANCE_NAME = "studio";

test.skip(process.env.F8_SCREENSHOT !== "1", "docs screenshot capture (set F8_SCREENSHOT=1)");

async function registerSecuredInstance(page: Page, name = INSTANCE_NAME) {
  await page.goto("/");
  await page.getByTestId("instance-add").click();
  await page.getByTestId("instance-name").fill(name);
  await page.getByTestId("instance-url").fill("");
  await page.getByLabel(/api key/i).fill(API_KEY);
  await page.getByTestId("instance-save").click();
  await page.getByRole("radio", { name: `activate ${name}` }).check();
  await expect(page.getByTestId("active-endpoint")).toContainText("same origin");
}

/** Load a curated sample through the gallery, which also builds the sample's index recipes. */
async function loadSample(page: Page, id: string) {
  await page.goto("/q/default/samples");
  // Entered on an empty graph: the first-run walkthrough opens itself over the gallery and its
  // scrim would swallow the load click below.
  await closeIntroIfOpen(page);
  await expect(page.getByTestId(`sample-card-${id}`)).toBeVisible({ timeout: 30_000 });
  await page.getByTestId(`load-sample-${id}`).click();
  // Only the WAIT may fail (a fresh graph loads with no wipe confirm). Wrapping the fill and the
  // click too would report a stuck confirm dialog as "loaded directly", then die much later on the
  // sample-message timeout naming the wrong cause.
  const typed = page.getByTestId("confirm-typed");
  let confirmShown = true;
  try {
    await typed.waitFor({ state: "visible", timeout: 2500 });
  } catch {
    confirmShown = false; // fresh graph: it loaded directly, no wipe confirm
  }
  if (confirmShown) {
    await typed.fill(INSTANCE_NAME);
    await page.getByTestId("confirm-action").click();
  }
  await expect(page.getByTestId("sample-message")).toContainText("Loaded", { timeout: 180_000 });
}

test("capture semantic search over the Movie Night embeddings index", async ({ page, request }) => {
  // Above the sum of the declared waits below (30 + 180 + 30 + 180), so a slow-but-working run
  // fails on the step that is slow rather than on the suite budget.
  test.setTimeout(480_000);
  await page.setViewportSize({ width: 1440, height: 1000 });

  // Refuse to run, rather than degrade the image, when the instance cannot embed text.
  const status = await request.get("/status", { headers: AUTH });
  expect(status.ok(), "GET /status must answer for the capture to be meaningful").toBeTruthy();
  const embedding = (await status.json()).embedding as { enabled?: boolean } | undefined;
  test.skip(
    embedding?.enabled !== true,
    "the embedding provider is off on this instance, so the semantic mode's query text is " +
      "disabled and this frame cannot be produced. Wire Fallen8__Embedding__* (see the header).",
  );

  await registerSecuredInstance(page);
  // Movie Night ships plot embeddings on its movie vertices AND an `embeddings` VectorIndex recipe
  // (samples/index.json), so the gallery load leaves that index built and populated.
  await loadSample(page, "movie-night");
  await expect(page.getByTestId("namespace-switcher")).toContainText("191 v", { timeout: 30_000 });

  await page.goto("/query");
  // Its own query mode since feature semantic-search-onramp: no index has to be picked first, and
  // the selector that follows offers only the indexes that can actually rank a vector.
  await page.getByTestId("query-mode").selectOption("semantic");
  await page.getByTestId("semantic-index-select").selectOption("embeddings");
  await page.getByTestId("vector-search-text").fill("mind-bending sci-fi about dreams");
  await page.locator("#vector-k").fill("10");
  await page.getByTestId("scan-run").click();

  await expect(page.getByTestId("vector-legend")).toBeVisible({ timeout: 180_000 });
  // The docs page's actual claim is that this sentence ranks Inception (vertex 0) top by cosine. If
  // the model or the corpus ever changes that, this fails rather than publishing a frame whose
  // surrounding prose no longer matches. Assert the id CELL, not the row: every cosine renders as
  // "0.xxxx" (ElementTable toFixed(4)), so a row-level check for "0" would pass on ANY ranking.
  const topRow = page.locator("tbody tr").first();
  await expect(topRow.locator("td").first()).toHaveText("0");
  // Vertex 0 IS Inception in movie-night.jsonl, so the exact id cell above is the whole rank-1 pin.
  //
  // There used to be a `toContainText("Inception")` here as corroboration. It is gone because it
  // could never corroborate anything: the properties cell prints properties in a nondeterministic
  // order and TRUNCATES, so the film name surviving into the visible text was luck. That luck ran
  // out once the provider-written `$embeddingModel:default=…` marker joined the list and pushed
  // `title=` past the cut - the row then read "year=2010, $embeddingModel…, plot=A thief who
  // steals…", which is Inception by every other measure and failed the assertion anyway. A check
  // that fails on a correct ranking is worse than no check; the id cell states the real claim.
  await page.screenshot({ path: "../docs/src/assets/images/query-semantic-search.png" });
});
