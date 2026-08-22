// MIT License
//
// screenshot-configuration.spec.ts
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
 * Docs screenshot capture: the configuration SURFACE as an editor (features
 * writable-instance-config and configuration-surface). Capture-only.
 *
 *   F8_SCREENSHOT=1 F8_UI_URL=http://127.0.0.1:<port> npx playwright test e2e/screenshot-configuration.spec.ts
 *
 * The capture app must be able to accept a write, or this photographs the wrong thing: run it with
 * Fallen8__Security__ApiKey, Fallen8__Security__EnableConfigurationWrite=true and
 * Fallen8__Metadata__Directory pointing at a FRESH directory. A metadata directory reused between
 * capture sessions keeps the config.overrides.json an earlier session wrote, which would show stale
 * "set here" badges and a phantom restart banner.
 *
 * Output: docs/src/assets/images/screen-configuration.png.
 *
 * Playwright runs the capture specs in filename order, so this one goes first and its PATCH below is
 * what puts a stored value and a restart-pending state into the two frames captured after it. That is
 * load-bearing rather than incidental: run it first if you run them individually.
 */

const API_KEY = process.env.F8_E2E_API_KEY ?? "e2e-key";
const AUTH = { Authorization: `Bearer ${API_KEY}` };

// A RESTART-tier key, so one written value puts all three things this surface exists for on the
// picture at once: the "set here" source badge, the restart-to-apply chip on the row, and the pending
// list naming the running and the pending value. It has to be restart-tier for the pending state to
// appear at all (a live key applies immediately and is correctly never pending).
//
// Change feed rather than a smaller section: its five keys fill the pane and cover three of the
// control shapes a reader should see (a checkbox for a bool, numeric fields, and a live-tier row
// beside restart-tier ones), while still fitting without scrolling.
const WRITTEN_KEY = "Fallen8:ChangeFeed:BufferSize";
const WRITTEN_VALUE = "16384";
const WRITTEN_ROW = "config-setting-fallen8-changefeed-buffersize";
const WRITTEN_SECTION = "config-section-changefeed";

test.skip(process.env.F8_SCREENSHOT !== "1", "docs screenshot capture (set F8_SCREENSHOT=1)");

test("capture the configuration surface", async ({ page, request }) => {
  await page.setViewportSize({ width: 1440, height: 1000 });

  // A stored value, so the shot shows what the feature is actually for: a row whose value came from
  // this instance's own configuration, its "set here" badge, and the restart banner naming both the
  // running and the pending value. Written over REST rather than through the UI so the picture is of
  // a settled state, not of a form mid-edit.
  const written = await request.patch("/config", {
    headers: AUTH,
    data: { settings: { [WRITTEN_KEY]: WRITTEN_VALUE } },
  });
  expect(
    written.ok(),
    "PATCH /config was refused, so the capture app cannot accept a write: " + (await written.text()),
  ).toBeTruthy();

  await page.goto("/");
  await page.getByTestId("instance-add").click();
  await page.getByTestId("instance-name").fill("secured");
  await page.getByTestId("instance-url").fill("");
  await page.getByLabel(/api key/i).fill(API_KEY);
  await page.getByTestId("instance-save").click();
  await page.getByRole("radio", { name: "activate secured" }).check();
  await expect(page.getByTestId("health-chip")).toContainText(/online/i, { timeout: 20_000 });

  await page.goto("/");
  await expect(page.getByTestId("configuration-panel")).toBeVisible({ timeout: 20_000 });
  // A POSITIVE load gate. Waiting for "checking…" to disappear stopped meaning anything once the
  // spinner moved inside the surface: it passes at zero matches, so the shot could catch a half
  // loaded card.
  await expect(page.getByTestId("config-settings-summary")).toContainText(/set here/, {
    timeout: 20_000,
  });

  // Open the surface and navigate to the section holding the written key.
  await page.getByTestId("config-configure").click();
  await expect(page.getByTestId("config-surface")).toBeVisible({ timeout: 20_000 });
  await page.getByTestId(WRITTEN_SECTION).click();

  // GUARDS: each one names what to set, because a silently degraded picture is the failure mode this
  // capture has to avoid.
  await expect(
    page.getByTestId(WRITTEN_ROW),
    "the settings editor is missing: set Fallen8__Security__ApiKey, " +
      "Fallen8__Security__EnableConfigurationWrite=true and Fallen8__Metadata__Directory.",
  ).toBeVisible({ timeout: 20_000 });
  await expect(
    page.getByTestId(WRITTEN_ROW),
    "the stored value did not survive to the read surface.",
  ).toHaveValue(WRITTEN_VALUE);
  await expect(
    page.getByTestId("config-pending-restart-detail"),
    "the pending-restart list is absent: a restart-tier value must differ from what this process " +
      "booted with, so start the capture app with a FRESH Fallen8__Metadata__Directory.",
  ).toBeVisible();
  await expect(page.getByTestId("config-pending-restart-detail")).toContainText(WRITTEN_KEY);
  // The written row must be IN the picture: its source badge is what shows that a value came from
  // this instance's own configuration.
  await expect(
    page.getByTestId(WRITTEN_ROW),
    "the written row is below the section pane's scroll, so the shot would not show a stored value.",
  ).toBeInViewport();
  // The section nav is the other half of what this image documents.
  await expect(page.getByTestId("config-section-nav")).toBeInViewport();

  // A whole-page shot: the surface is a modal, so a portal is NOT a descendant of the Connect card,
  // and locating that card would photograph a summary with no settings on it.
  await page.screenshot({ path: "../docs/src/assets/images/screen-configuration.png" });
});
