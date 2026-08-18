// MIT License
//
// screenshot-nl-assist.spec.ts
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

import { readFileSync } from "node:fs";
import path from "node:path";
import { expect, test, type Page } from "@playwright/test";

/**
 * Docs screenshot capture for the delegate editor's NL-assist panel (features nl-assist /
 * nl-assist-ux). Output: docs/src/assets/images/screen-nl-assist.png
 *
 *   F8_SCREENSHOT=1 npm run e2e -- screenshot-nl-assist
 *
 * WHY THE MODEL IS STUBBED (the same reasoning as screenshot-integrations.spec.ts): in the default
 * instance mode the panel calls browser -> POST /chat -> the instance's Ollama sidecar. The capture
 * server is a bare apiApp: the chat gateway defaults OFF (Fallen8:Chat:Enabled) and there is no
 * sidecar, so a real draft would need a model running AND would sample differently on every run.
 *
 * Exactly ONE thing is faked here: the chat completion body (the shape ChatController returns). Every
 * step downstream is the product's own: the drafts are pretty-printed by format.ts, inserted into the
 * real Monaco editor, and validated by the real POST /delegates/validate - so the green ticks, the
 * VALID badge and the multi-line layout are Fallen-8's verdict on the exact text on screen, not a
 * mock's. The fragments are also true of the seeded sample graph (attack-surface: label "user" with
 * a "department" property), so nothing in the frame claims something the data would not support.
 */

const API_KEY = process.env.F8_E2E_API_KEY ?? "e2e-key";
const AUTH = { Authorization: `Bearer ${API_KEY}` };
const NDJSON = { ...AUTH, "Content-Type": "application/x-ndjson" };

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

/**
 * One canned completion per call, in order. All three are shapes the snippet library already
 * teaches, so Roslyn accepts them; the middle one is long enough that format.ts breaks it across
 * lines, which is what the editor then shows.
 */
const DRAFTS = [
  'return (v) => v.Label == "user";',
  'return (v) => v.Label == "user" && v.TryGetProperty(out string department, "department") && department == "Finance";',
  'return (v) => v.Label == "user" && v.GetOutDegree() + v.GetInDegree() >= 3;',
];

const INTENTS = [
  "only users",
  "users in the finance department",
  "users with at least three connections",
];

test("capture NL assist with drafted, server-validated fragments", async ({ page, request }) => {
  // The modal is a fixed 1024px wide (w-5xl) and 80vh tall, and the NL panel shares its sidebar
  // with the snippet list: below ~960px of viewport height the sidebar scrolls and clips the
  // export affordance, so the capture height is load-bearing, not cosmetic: the sidebar needs about
  // 700px here (snippets, the intent box, three drafts and the export link) against a 0.8x modal.
  // 1440 wide matches the rest of the Studio image set.
  await page.setViewportSize({ width: 1440, height: 940 });

  // A real dataset behind the modal, so the top bar's counts and the slot context are not zeroes.
  const jsonl = readFileSync(
    path.resolve(process.cwd(), "../samples/attack-surface.jsonl"),
    "utf8",
  );
  await request.head("/tabularasa/all", { headers: AUTH });
  expect((await request.post("/bulk/import", { headers: NDJSON, data: jsonl })).ok()).toBeTruthy();

  // Stand in for the instance's chat gateway. The stats block mirrors what OllamaChatBackend
  // forwards (prompt/completion tokens, duration, tok/s), so the per-draft stats line renders.
  let call = 0;
  await page.route("**/chat", (route) => {
    const content = DRAFTS[Math.min(call, DRAFTS.length - 1)];
    call += 1;
    return route.fulfill({
      status: 200,
      contentType: "application/json",
      body: JSON.stringify({
        content,
        model: "phi4-f8-mini",
        stats: {
          promptTokens: 1148 + call * 7,
          completionTokens: 18 + call * 3,
          durationMs: 1810 + call * 90,
          tokensPerSecond: 38.6,
        },
      }),
    });
  });

  await registerSecuredInstance(page);

  // The NL panel lives in the delegate editor, which opens from a fragment slot on Path.
  await page.goto("/path");
  // The modal header echoes the endpoints as "Path finder · 1 → 42"; unset they render as "? → ?".
  await page.getByTestId("path-from").fill("1");
  await page.getByTestId("path-to").fill("42");
  await page.getByTestId("toggle-advanced").click();
  await page.getByTestId("slot-filter-vertexfilter").click();
  await expect(page.getByTestId("nl-intent")).toBeVisible();
  await expect(page.getByTestId("nl-backend-status")).toContainText("this instance");

  // Three intents -> three drafts, so the list is a genuine history rather than one row.
  for (const intent of INTENTS) {
    await page.getByTestId("nl-intent").fill(intent);
    await page.getByTestId("nl-generate").click();
    // The button label is the busy state; back to its resting text = the loop finished.
    await expect(page.getByTestId("nl-generate")).toHaveText("Draft fragment");
  }
  await expect(page.getByTestId("nl-attempts").locator("li")).toHaveCount(3);
  // The server validated the drafted text: this badge is Fallen-8's, not the stub's.
  await expect(page.getByTestId("validation-valid")).toBeVisible();

  // Judge the oldest draft: it loses the awaiting-review highlight (the newer two keep it) and
  // the training-example export appears.
  await page.getByTestId("nl-verdict-0").getByRole("button").first().click();
  await expect(page.getByTestId("nl-export-training")).toBeVisible();

  // Crop to the top bar plus the modal, the same framing as screenshot-delegate-editor.spec.ts:
  // it drops the empty band the centered 80vh modal leaves below itself. `clip` is a viewport
  // crop, so nothing rendered above the fold is lost.
  const dialog = await page.locator('[role="dialog"]').boundingBox();
  if (!dialog) throw new Error("the delegate editor dialog has no box");
  await page.screenshot({
    path: "../docs/src/assets/images/screen-nl-assist.png",
    clip: { x: 0, y: 0, width: 1440, height: Math.ceil(dialog.y + dialog.height + 22) },
  });
});
