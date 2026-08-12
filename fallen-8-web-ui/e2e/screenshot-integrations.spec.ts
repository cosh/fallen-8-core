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
 * returns, so serving the descriptors of the three SHIPPED integrations - copied from their own
 * descriptor definitions - captures exactly what a user with the sidecar configured sees.
 *
 * The consequence to respect when editing: if a shipped descriptor changes (a setting added, a
 * label reworded), this fixture has to change with it or the screenshot starts telling a story the
 * runtime no longer tells. The screen has real tests against the real contract shape
 * (tests/integrations-screen.test.tsx); this file exists only to photograph it.
 */

test.skip(process.env.F8_SCREENSHOT !== "1", "docs screenshot capture (set F8_SCREENSHOT=1)");

const SHIPPED_DESCRIPTORS = [
  {
    id: "csv-inventory",
    displayName: "CSV inventory",
    description:
      "Reads a CSV of things you already track - a spreadsheet export, an asset list - from the " +
      "runtime's files directory.",
    settings: [
      {
        key: "file",
        label: "File",
        kind: "Text",
        required: true,
        help: "A file name inside the runtime's files directory.",
        defaultValue: "devices.csv",
      },
      {
        key: "keyColumn",
        label: "Key column",
        kind: "Text",
        required: true,
        help: "The column whose value identifies a row across runs.",
      },
      {
        key: "keyType",
        label: "Key type",
        kind: "Text",
        required: true,
        help: "Which identifier the key column holds, for example mac or serial.",
        defaultValue: "mac",
      },
      {
        key: "label",
        label: "Label",
        kind: "Text",
        required: false,
        help: "What to call the elements this integration creates.",
        defaultValue: "device",
      },
    ],
    entityKinds: ["device"],
    claimTypes: ["mac", "serial", "hostname"],
    relationTypes: [],
    requiresCredential: false,
  },
  {
    id: "unifi-network",
    displayName: "UniFi Network",
    description:
      "Reads a UniFi console on your own network: the site, its devices and the clients they see.",
    settings: [
      {
        key: "baseUrl",
        label: "Console URL",
        kind: "Url",
        required: true,
        help: "The console's address, for example https://192.168.1.1.",
      },
      {
        key: "apiKey",
        label: "API key",
        kind: "Credential",
        required: true,
        help: "Created in the console under Settings, then Admins, then API keys.",
      },
      {
        key: "site",
        label: "Site",
        kind: "Text",
        required: false,
        help: "Which site to read; the default site when omitted.",
        defaultValue: "default",
      },
      {
        key: "trustSelfSigned",
        label: "Trust the console's own certificate",
        kind: "Boolean",
        required: false,
        help: "A console on your own network usually presents a self-signed certificate.",
      },
    ],
    entityKinds: ["site", "device", "client"],
    claimTypes: ["mac", "serial", "hostname", "ipv4"],
    relationTypes: ["site", "connectedTo"],
    requiresCredential: true,
  },
  {
    id: "fronius-solar",
    displayName: "Fronius solar",
    description:
      "Reads a Fronius inverter's shape and coarse state - what the installation IS, never a time " +
      "series of readings.",
    settings: [
      {
        key: "baseUrl",
        label: "Inverter URL",
        kind: "Url",
        required: true,
        help: "The inverter's address on your network.",
      },
      {
        key: "label",
        label: "Label",
        kind: "Text",
        required: false,
        help: "What to call the elements this integration creates.",
        defaultValue: "inverter",
      },
    ],
    entityKinds: ["inverter", "logger"],
    claimTypes: ["serial"],
    relationTypes: ["loggedBy"],
    requiresCredential: false,
  },
];

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

  // Open the UniFi entry: it is the one with a credential, a boolean and a defaulted setting, so the
  // form shows every control kind the descriptor model can ask for.
  await page.getByTestId("integration-select-unifi-network").click();
  await expect(page.getByTestId("integration-instance-id")).toBeVisible();
  await page.getByTestId("integration-instance-id").fill("office");

  await page.screenshot({
    path: "../docs/src/assets/images/screen-integrations.png",
    fullPage: false,
  });
});
