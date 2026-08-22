// MIT License
//
// screenshot-connect.spec.ts
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
 * Docs screenshot capture: the Connect screen with the active same-origin "local" instance
 * (registration list + configuration). Capture-only; needs no graph data.
 *
 *   F8_SCREENSHOT=1 F8_UI_URL=http://127.0.0.1:<port> npx playwright test e2e/screenshot-connect.spec.ts
 *
 * Output: docs/src/assets/images/screen-connect.png.
 */

const API_KEY = process.env.F8_E2E_API_KEY ?? "e2e-key";
const AUTH = { Authorization: `Bearer ${API_KEY}` };

test.skip(process.env.F8_SCREENSHOT !== "1", "docs screenshot capture (set F8_SCREENSHOT=1)");

test("capture the Connect screen", async ({ page, request }) => {
  // Back down from the 1600 the inline settings list needed: those rows moved into the configuration
  // surface (feature configuration-surface), so the Configuration card is a summary again. Still
  // taller than the original 900, because the card carries the two provider cards and the
  // Namespaces panel below it has to stay in frame. Both toBeInViewport guards below are the
  // tripwire: at 900 the Namespaces rows went off the bottom while every assertion still passed.
  await page.setViewportSize({ width: 1440, height: 1200 });

  await request.head("/tabularasa/all", { headers: AUTH });

  // Two namespaces beside "default", so the "at startup" column shows a real per-namespace value
  // instead of "inherit" everywhere: one inherits the server default, one is explicitly excluded
  // from the next boot (feature namespace-startup-load). The policy is persisted server-side and
  // changes nothing in the running process, so the capture stays a pure screenshot fixture.
  await request.put("/ns/flights", { headers: AUTH });
  await request.put("/ns/archive-2024", { headers: AUTH });
  const excluded = await request.patch("/ns/archive-2024", {
    headers: AUTH,
    data: { loadOnStartup: "disabled" },
  });
  expect(excluded.ok(), await excluded.text()).toBeTruthy();

  // The auto-seeded same-origin "local" default has no persistent key (feature standalone-ui),
  // so on a secured server it reads "unauthorized"; register a keyed same-origin instance with a
  // distinct name so the list clearly shows the default alongside an authorized one.
  await page.goto("/");
  await page.getByTestId("instance-add").click();
  await page.getByTestId("instance-name").fill("secured");
  await page.getByTestId("instance-url").fill("");
  await page.getByLabel(/api key/i).fill(API_KEY);
  await page.getByTestId("instance-save").click();
  await page.getByRole("radio", { name: "activate secured" }).check();
  await expect(page.getByTestId("active-endpoint")).toContainText("same origin");
  await expect(page.getByTestId("health-chip")).toContainText(/online/i, { timeout: 20_000 });

  // Connect is the root route; it stays reachable in every connection state.
  await page.goto("/");
  await expect(page.getByTestId("instance-add")).toBeVisible({ timeout: 20_000 });

  // Wait for the inventory itself, so the Namespaces panel is on the picture with its rows and
  // the "at startup" column populated rather than mid-fetch.
  await expect(page.getByTestId("namespace-row-archive-2024")).toBeVisible({ timeout: 20_000 });
  await expect(page.getByTestId("namespace-startup-archive-2024")).toHaveValue("disabled");

  // Each instance's health cell is a lazy GET /status, and the reload above restarts it, so wait
  // for every one of them to resolve - otherwise the picture catches them mid-"checking…".
  await expect(page.getByText("checking…")).toHaveCount(0, { timeout: 20_000 });

  // GUARD: the Configuration section is part of this shot and is only worth photographing when
  // the instance actually has its exporter and providers wired. Run the capture app with
  // Fallen8__Observability__Otlp__Endpoint, Fallen8__Embedding__* and Fallen8__Chat__*.
  await expect(
    page.getByTestId("config-observability-summary"),
    'the Configuration section reads "Off - no exporter configured": set ' +
      "Fallen8__Observability__Otlp__Endpoint on the capture app.",
  ).toContainText("pushing metrics");
  await expect(
    page.getByTestId("config-embedding"),
    "the Embedding card is unconfigured: this shot documents the provider cards, so wire " +
      "Fallen8__Embedding__* on the capture app.",
  ).toContainText("Ollama");
  await expect(
    page.getByTestId("config-chat"),
    "the Chat card is unconfigured: wire Fallen8__Chat__* on the capture app.",
  ).toContainText("Ollama");

  // GUARD: the settings themselves are NOT on this shot any more, and asserting a row here would be
  // false by construction (they are in a modal). What this shot documents is the summary and the way
  // in, so the inventory count and the Configure button are the things that have to be real: the
  // count is only non-zero when the capture app actually published an inventory.
  await expect(
    page.getByTestId("config-settings-summary"),
    "the settings inventory is missing: run the capture app with Fallen8__Metadata__Directory so it " +
      "has somewhere to store configuration.",
  ).toContainText(/set here/, { timeout: 20_000 });
  await expect(page.getByTestId("config-configure")).toBeVisible();
  await expect(
    page.locator('[data-testid^="config-setting-"]'),
    "a settings row is on the Connect screen, so the configuration surface did not take them.",
  ).toHaveCount(0);
  await expect(
    page.getByTestId("namespaces-panel"),
    "the Namespaces panel is off the picture: the viewport is too short.",
  ).toBeVisible();
  // Every namespace row in frame, not just the first two: the "at startup" column is part of what this
  // shot documents, and the inheriting row is the one that shows what inherit resolves to.
  await expect(
    page.getByTestId("namespace-startup-flights"),
    "the last namespace row is below the fold: the viewport is too short.",
  ).toBeInViewport();

  await page.screenshot({ path: "../docs/src/assets/images/screen-connect.png" });
});
