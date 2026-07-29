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

/**
 * Docs screenshot capture (feature stored-query-scenario-scoped-ux): stored queries are
 * unique to their scenario, so the management panel now lives on the Path and Subgraph
 * screens (kind-scoped) and no longer on the Query screen. Capture-only. Outputs:
 *   docs/images/screen-query.png             (Query: no stored-query section)
 *   docs/images/screen-path.png              (Path: Stored path queries panel)
 *   docs/images/screen-subgraph-builder.png  (Subgraph: Stored subgraph queries panel)
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

test("capture the relocated, scenario-scoped stored-query panels", async ({ page }) => {
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

  // Query screen: property/index scans only — the stored-query section is gone.
  await page.goto("/query");
  await expect(page.getByTestId("query-mode")).toBeVisible();
  await expect(page.getByTestId("stored-queries-Path")).toHaveCount(0);
  await expect(page.getByTestId("stored-queries-SubGraph")).toHaveCount(0);
  await page.screenshot({ path: "../docs/src/assets/images/screen-query.png" });

  // Path screen: the kind-scoped Stored path queries panel with its two Path entries.
  await page.goto("/path");
  await expect(page.getByTestId("stored-queries-Path")).toBeVisible();
  await expect(page.getByText("adults-shortest")).toBeVisible();
  await page.screenshot({ path: "../docs/src/assets/images/screen-path.png", fullPage: true });

  // Subgraph screen: the kind-scoped Stored subgraph queries panel with its two entries.
  await page.goto("/subgraphs");
  await expect(page.getByTestId("stored-queries-SubGraph")).toBeVisible();
  await expect(page.getByText("person-neighborhood")).toBeVisible();
  await page.screenshot({ path: "../docs/src/assets/images/screen-subgraph-builder.png", fullPage: true });
});
