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
  await page.setViewportSize({ width: 1600, height: 1000 });

  // Stand in for the proxied sidecar. Only the descriptor list is served: the screenshot shows the
  // form as it opens, so no job is ever submitted.
  await page.route("**/integrations/providers", (route) =>
    route.fulfill({
      status: 200,
      contentType: "application/json",
      body: JSON.stringify(SHIPPED_DESCRIPTORS),
    }),
  );

  await page.goto("/integrations");

  // The catalog drives everything on this screen, so wait for it rather than for a timeout.
  await expect(page.getByTestId("integration-select-unifi-network")).toBeVisible();

  // Open the UniFi entry: it is the shipped integration that asks for a credential, so the form
  // shows the password control and the help text saying the value is held for the run alone.
  await page.getByTestId("integration-select-unifi-network").click();
  await expect(page.getByTestId("integration-instance-id")).toBeVisible();
  await page.getByTestId("integration-instance-id").fill("office");

  await page.screenshot({
    path: "../docs/src/assets/images/screen-integrations.png",
    fullPage: false,
  });
});
