// MIT License
//
// screenshot-integrations.spec.ts
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

import shippedDescriptors from "../../features/done/integrations/provider-descriptors.json" with { type: "json" };
import type { IntegrationProvider } from "../src/api/types";

/**
 * Docs screenshot capture for the Integrations screen (feature integrations).
 *
 *   F8_SCREENSHOT=1 npx playwright test e2e/screenshot-integrations.spec.ts
 *
 * Output: docs/src/assets/images/screen-integrations.png
 *
 * WHY THIS ONE STUBS ITS BACKEND, unlike every other capture here: the integrations runtime is a
 * SEPARATE DEPLOYABLE that the apiApp only proxies (/integrations/*), and its container port is
 * deliberately never published. The default e2e webServer runs the apiApp and the UI, not that
 * sidecar, so there is no honest way to make the real descriptor list appear without also starting
 * a second service (and, for the UniFi and Fronius entries, a console and an inverter on the
 * network). What the screen renders, though, is a pure function of the descriptor list the proxy
 * returns, so replaying the pinned snapshot of what GET /integration/providers serves captures what
 * a user with the sidecar configured sees.
 *
 * Nothing here is hand-copied, which is the point: the snapshot is generated from the shipped
 * descriptors and ProviderDescriptorSnapshotTest fails when the runtime drifts from it, so a
 * reworded label cannot leave this capture photographing a form the product does not have. The
 * screen's own behaviour is tested against the contract shape in tests/integrations-screen.test.tsx;
 * this file exists only to photograph it.
 */

test.skip(process.env.F8_SCREENSHOT !== "1", "docs screenshot capture (set F8_SCREENSHOT=1)");

const SHIPPED_DESCRIPTORS = shippedDescriptors as IntegrationProvider[];

test("capture the Integrations screen rendered from the shipped descriptors", async ({ page }) => {
  await page.setViewportSize({ width: 1600, height: 1180 });

  // Stand in for the proxied sidecar. Only the descriptor list is served: the screenshot shows the
  // form as it opens, so no job is ever submitted.
  await page.route("**/integrations/providers", (route) =>
    route.fulfill({
      status: 200,
      contentType: "application/json",
      body: JSON.stringify(SHIPPED_DESCRIPTORS),
    }),
  );

  // Stand in for an instance whose embedding provider is ON, which is what the compose default
  // gives (F8_EMBEDDINGS defaults true). The capture app deliberately has no provider configured, so
  // without this the page would photograph the embed opt-in in its DISABLED state - a dead control,
  // which is the exact failure this file's descriptor stub exists to avoid. Only the embedding block
  // is asserted; everything else in the status answer passes through untouched.
  await page.route("**/status", async (route) => {
    const response = await route.fetch();
    const status = await response.json();
    route.fulfill({
      response,
      contentType: "application/json",
      body: JSON.stringify({
        ...status,
        embedding: {
          enabled: true,
          backend: "Ollama",
          modelName: "bge-m3",
          modelVersion: "",
          dimension: 1024,
          intendedMetric: "Cosine",
          loaded: false,
        },
      }),
    });
  });

  await page.goto("/integrations");

  // The catalog drives everything on this screen, so wait for it rather than for a timeout.
  await expect(page.getByTestId("integration-select-unifi-network")).toBeVisible();

  // Open the CSV entry: it is a shipped integration that asks for a FILE, so the form shows the
  // dropzone and the picker somebody actually uses. Deliberately not the UniFi one any more - that
  // photographed the credential control, and the credential story is prose the page already tells,
  // whereas "you upload the file with the run" is a claim a screenshot of a text box contradicts.
  await page.getByTestId("integration-select-csv-device-list").click();
  await expect(page.getByTestId("integration-instance-id")).toBeVisible();
  await page.getByTestId("integration-instance-id").fill("office");

  // Ticked, because the point the page makes about it is that the TEMPLATE is visible before the run
  // rather than inferred after it - and the template only renders once the opt-in is on.
  await page.getByTestId("integration-embed-toggle").click();
  await expect(page.getByTestId("integration-embed-template")).toBeVisible();

  await page.screenshot({
    path: "../docs/src/assets/images/screen-integrations.png",
    fullPage: false,
  });
});
