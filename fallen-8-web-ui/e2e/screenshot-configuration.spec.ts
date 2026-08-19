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
 * Docs screenshot capture: the Configuration panel as an EDITOR (feature
 * writable-instance-config). Capture-only.
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
 */

const API_KEY = process.env.F8_E2E_API_KEY ?? "e2e-key";
const AUTH = { Authorization: `Bearer ${API_KEY}` };

// A RESTART-tier key that sits in the FIRST few catalogued rows, so one written value puts all three
// things this panel exists for on the picture at once: the "set here" source badge, the restart-to-apply
// chip on the row, and the banner naming the running and the pending value. It has to be restart-tier
// for the banner to appear at all: a live key applies immediately and is correctly never pending, and
// it has to be near the top because the settings list caps its own height and scrolls.
const WRITTEN_KEY = "Fallen8:Analytics:MaxTimeBudgetSeconds";
const WRITTEN_ROW = "config-setting-fallen8-analytics-maxtimebudgetseconds";

test.skip(process.env.F8_SCREENSHOT !== "1", "docs screenshot capture (set F8_SCREENSHOT=1)");

test("capture the Configuration editor", async ({ page, request }) => {
  await page.setViewportSize({ width: 1280, height: 1000 });

  // A stored value, so the shot shows what the feature is actually for: a row whose value came from
  // this instance's own configuration, its "set here" badge, and the restart banner naming both the
  // running and the pending value. Written over REST rather than through the UI so the picture is of
  // a settled state, not of a form mid-edit.
  const written = await request.patch("/config", {
    headers: AUTH,
    data: { settings: { [WRITTEN_KEY]: "600" } },
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
  await expect(page.getByText("checking…")).toHaveCount(0, { timeout: 20_000 });

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
  ).toHaveValue("600");
  await expect(
    page.getByTestId("config-pending-restart"),
    "the restart banner is absent: a restart-tier value must differ from what this process booted " +
      "with, so start the capture app with a FRESH Fallen8__Metadata__Directory.",
  ).toBeVisible();
  await expect(page.getByTestId("config-pending-restart")).toContainText(WRITTEN_KEY);
  // The written row must be IN the picture, not below the list's scroll cap: the source badge is what
  // shows that a value came from this instance's own configuration.
  await expect(
    page.getByTestId(WRITTEN_ROW),
    "the written row is below the settings list's scroll cap, so the shot would not show a stored value.",
  ).toBeInViewport();

  // Scroll the panel into frame and shoot it alone: the settings list is the subject, and a
  // whole-page shot at this height would reduce it to a band.
  const panel = page.getByTestId("configuration-panel");
  await panel.scrollIntoViewIfNeeded();
  await panel.screenshot({ path: "../docs/src/assets/images/screen-configuration.png" });
});
