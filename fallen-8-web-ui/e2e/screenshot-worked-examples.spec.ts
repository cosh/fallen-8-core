// MIT License
//
// screenshot-worked-examples.spec.ts
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
 * Docs screenshot capture for the two worked-result frames on the Samples page. Outputs:
 *   docs/src/assets/images/path-result.png      (samples.md: a path result)
 *   docs/src/assets/images/subgraph-result.png  (samples.md: a created subgraph)
 *
 *   F8_SCREENSHOT=1 npm run e2e -- screenshot-worked-examples
 *
 * WHY THIS SPEC EXISTS: both images were published by hand in 2026-07 and no spec produced them, so
 * every later recapture pass silently skipped them and they went stale by six UI features: an
 * 11-entry nav rail against today's 15, no help button, no events bell, and copy that has since
 * changed. The ids in them were always right, though: the engine assigns ids at import rather than
 * taking them from the sample file, so the loaded karate club is 0..33 no matter what
 * karate-club.jsonl numbers its rows, and 0 -> 33 (Mr. Hi to the Officer) is the pair the sample's
 * own try-step suggests.
 */

const API_KEY = process.env.F8_E2E_API_KEY ?? "e2e-key";
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
  await expect(page.getByTestId(`sample-card-${id}`)).toBeVisible({ timeout: 30_000 });
  await page.getByTestId(`load-sample-${id}`).click();
  const typed = page.getByTestId("confirm-typed");
  try {
    await typed.waitFor({ state: "visible", timeout: 2500 });
    await typed.fill(INSTANCE_NAME);
    await page.getByTestId("confirm-action").click();
  } catch {
    // fresh graph: it loaded directly, no wipe confirm
  }
  await expect(page.getByTestId("sample-message")).toContainText("Loaded", { timeout: 120_000 });
}

test("capture a path result and a created subgraph on the karate club", async ({ page }) => {
  // Above the sum of the declared waits below (30 + 120 + 30 + 30 + 60), so a slow-but-working run
  // fails on the step that is slow rather than on the suite budget.
  test.setTimeout(330_000);
  // 1440x1000 matches the sibling screen-path.png / screen-subgraph-builder.png frames the same
  // docs page embeds; the orphaned originals were 1600 wide and read as a different application.
  await page.setViewportSize({ width: 1440, height: 1000 });

  await registerSecuredInstance(page);
  await loadSample(page, "karate-club");
  // The top bar must carry the real counts before either shot, or the frame photographs 0 v.
  await expect(page.getByTestId("namespace-switcher")).toContainText("34 v", { timeout: 30_000 });

  // ---- path-result.png ---------------------------------------------------------------------
  await page.goto("/path");
  await page.getByTestId("path-from").fill("0"); // Mr. Hi
  await page.getByTestId("path-to").fill("33"); // the Officer
  // BLS / 7 / 1 are the draft defaults. Assert them rather than retyping, so a default change
  // fails here instead of silently changing the published frame.
  await expect(page.getByTestId("path-algo")).toHaveValue("BLS");
  await expect(page.locator("#path-depth")).toHaveValue("7");
  await expect(page.locator("#path-results")).toHaveValue("1");
  await page.getByTestId("path-run").click();

  await expect(page.getByTestId("path-weight-0")).toBeVisible({ timeout: 30_000 });
  // Mr. Hi and the Officer have no direct edge but share neighbours, so BLS finds a two-hop route.
  // The hop count is the claim the docs page makes; which of the four routes wins is traversal
  // order and deliberately not asserted.
  await expect(page.getByText(/2 hop\(s\)/)).toBeVisible();
  await expect(page.getByTestId("path-weight-0")).toHaveText("0");
  await page.screenshot({ path: "../docs/src/assets/images/path-result.png" });

  // ---- subgraph-result.png -----------------------------------------------------------------
  // Subgraphs OUTLIVE the sample reload that wipes the graph, so on a second run the create would
  // be rejected as a duplicate and no message would ever render. Clear it over REST rather than
  // through the table: while the list loads, the table shows "no subgraphs yet", so a UI branch
  // reads whichever state it happened to catch.
  await page.request.delete("/subgraph/people-net", {
    headers: { Authorization: `Bearer ${API_KEY}` },
  });
  await page.goto("/subgraphs");
  await page.getByTestId("sg-name").fill("people-net");
  // The alternating vertex, edge, vertex pattern the prose describes. Every filter slot stays on
  // its "match everything" default, so the whole club is extracted.
  await page.getByTestId("add-vertex-step").click();
  await page.getByTestId("add-edge-step").click();
  await page.getByTestId("add-vertex-step").click();
  await page.getByTestId("sg-create").click();

  await expect(page.getByTestId("subgraph-message")).toContainText("Created 'people-net'", {
    timeout: 60_000,
  });
  // The extracted graph is the whole club. That equality is the point of the frame, so pin it.
  await expect(page.getByTestId("subgraph-message")).toContainText("34 vertices, 78 edges");
  // Crop to the Subgraphs panel and its message. Uncropped, four fifths of the frame repeats the
  // builder form that screen-subgraph-builder.png already shows two lines earlier on the same docs
  // page, which leaves the actual result in a sliver at the top.
  const message = await page.getByTestId("subgraph-message").boundingBox();
  if (!message) throw new Error("the subgraph message has no box");
  await page.screenshot({
    path: "../docs/src/assets/images/subgraph-result.png",
    clip: { x: 0, y: 0, width: 1440, height: Math.ceil(message.y + message.height + 20) },
  });
});
