// MIT License
//
// screenshot-stored-queries.spec.ts
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
 * Docs screenshot capture for the traversal surface and the stored-query library.
 *
 * The library is ONE `/storedquery` collection per namespace; two features decided where it is
 * SHOWN. stored-query-scenario-scoped-ux took it off the Query screen, where it sat on an
 * unrelated surface, and studio-traverse-merge replaced the two kind-scoped panels at the foot
 * of two near-twin screens with one table on the Traverse screen's third tab. Capture-only.
 * Outputs:
 *   docs/src/assets/images/screen-query.png            (Query: no stored-query section)
 *   docs/src/assets/images/screen-path.png             (Traverse: Path finding tab)
 *   docs/src/assets/images/screen-subgraph-builder.png (Traverse: Subgraph builder tab)
 *   docs/src/assets/images/screen-stored-queries.png   (Traverse: Stored queries tab)
 *
 * Run: F8_SCREENSHOT=1 npm run e2e -- screenshot-stored-queries
 */

const API_KEY = process.env.F8_E2E_API_KEY ?? "e2e-key";

test.skip(process.env.F8_SCREENSHOT !== "1", "docs screenshot capture (set F8_SCREENSHOT=1)");

async function registerSecuredInstance(page: Page, name = "studio") {
  await page.goto("/");
  await page.getByTestId("instance-add").click();
  await page.getByTestId("instance-name").fill(name);
  await page.getByTestId("instance-url").fill("");
  await page.getByLabel(/api key/i).fill(API_KEY);
  await page.getByTestId("instance-save").click();
  await page.getByRole("radio", { name: `activate ${name}` }).check();
  await expect(page.getByTestId("active-endpoint")).toContainText("same origin");
}

/** Register a stored query straight over REST (bare route aliases the default namespace). */
async function seed(page: Page, body: Record<string, unknown>) {
  const res = await page.request.post("/storedquery", {
    headers: { Authorization: `Bearer ${API_KEY}` },
    data: body,
  });
  // 201 fresh, 409 if a reused server already holds it — both leave the library populated.
  expect([201, 409]).toContain(res.status());
}

test("capture the three Traverse tabs and the Query screen without the library", async ({
  page,
}) => {
  await page.setViewportSize({ width: 1440, height: 1000 });
  await registerSecuredInstance(page);

  await seed(page, {
    name: "adults-shortest",
    kind: "Path",
    description: "age>30 people, weight by distance",
    path: {
      filter: { vertexFilter: 'return (v) => v.Label == "person";' },
      cost: { edgeCost: "return (e) => 1.0;" },
    },
  });
  await seed(page, {
    name: "knows-hops",
    kind: "Path",
    description: "traverse only 'knows' edges",
    path: { filter: { edgePropertyFilter: 'return (p) => p == "knows";' } },
  });
  await seed(page, {
    name: "person-neighborhood",
    kind: "SubGraph",
    description: "a person and their one-hop edges",
    subGraph: {
      vertexFilter: 'return (v) => v.Label == "person";',
      patterns: [
        { type: "Vertex", patternName: "start" },
        { type: "Edge" },
        { type: "Vertex", patternName: "next" },
      ],
    },
  });
  await seed(page, {
    name: "all-vertices",
    kind: "SubGraph",
    description: "match every vertex",
    subGraph: { patterns: [{ type: "Vertex" }] },
  });

  // Query screen: property/index scans only. The library is not here in ANY shape, which is
  // what the prefix locator says - it would also catch a kind-scoped panel coming back.
  await page.goto("/query");
  // Empty graph on purpose (this shot is about registrations, not data), so the first-run
  // walkthrough opens itself over each of the screens below and would be the picture.
  await closeIntroIfOpen(page);
  await expect(page.getByTestId("query-mode")).toBeVisible();
  await expect(page.locator('[data-testid^="stored-queries-"]')).toHaveCount(0);
  await page.screenshot({ path: "../docs/src/assets/images/screen-query.png" });

  // Traverse, tab 1: the path form, entered through the tab's own deep link. Every frame below
  // carries the tab strip, so the docs page can show where the two forms went.
  await page.goto("/q/default/traverse?tab=path");
  await expect(page.getByTestId("path-from")).toBeVisible();
  await page.screenshot({ path: "../docs/src/assets/images/screen-path.png", fullPage: true });

  // Tab 2 and 3 by CLICKING the strip rather than by URL: the frames are meant to show the
  // switcher working, and a click is what the reader will do.
  await page.getByTestId("traverse-tab-subgraph").click();
  await expect(page.getByTestId("sg-name")).toBeVisible();
  await page.screenshot({ path: "../docs/src/assets/images/screen-subgraph-builder.png", fullPage: true });

  // Tab 3: the unified library. Both kinds in one table with the kind column that only this
  // view has, and all four seeded entries - the count in the tab label is the same fact.
  await page.getByTestId("traverse-tab-stored").click();
  const library = page.getByTestId("stored-queries-all");
  await expect(library).toBeVisible();
  await expect(library.getByRole("columnheader", { name: "kind" })).toBeVisible();
  for (const [name, kind] of [
    ["adults-shortest", "Path"],
    ["knows-hops", "Path"],
    ["person-neighborhood", "SubGraph"],
    ["all-vertices", "SubGraph"],
  ]) {
    await expect(page.getByTestId(`stored-query-kind-${name}`)).toHaveText(kind);
  }
  // Label and count share one button with no separator, hence the run-together text. Pinned to
  // the four seeded entries: this spec is the only one that registers any, so a fifth would mean
  // the capture server is not the throwaway it is supposed to be - and the frame would lie.
  await expect(page.getByTestId("traverse-tab-stored")).toHaveText(/^Stored queries\s*4$/);
  // Cropped to the table: at 1000px the library tab leaves two thirds of the frame empty, which
  // reads as a broken screen rather than a short list.
  const box = await library.boundingBox();
  if (!box) throw new Error("the stored-query library has no box");
  await page.screenshot({
    path: "../docs/src/assets/images/screen-stored-queries.png",
    clip: { x: 0, y: 0, width: 1440, height: Math.ceil(box.y + box.height + 20) },
  });
});
