// MIT License
//
// screenshot-delegate-editor.spec.ts
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
 * Docs screenshot capture for the delegate editor's static IntelliSense (feature web-ui FR-22).
 * Output: docs/src/assets/images/screen-delegate-editor.png
 *
 *   F8_SCREENSHOT=1 npm run e2e -- screenshot-delegate-editor
 *
 * The shot has to catch a Monaco suggest widget mid-flight, which makes three things load-bearing:
 *
 *  - The fragment is typed in ONE keyboard.type() call. Monaco over-types its own auto-closed `)`
 *    and `"`, so splitting the string around them leaves a surplus `)` and validation never goes
 *    green.
 *  - The list is re-opened by deleting and retyping the `.`, never by Ctrl+Space. Our provider
 *    declares `.` as its trigger character (src/delegate/providers.ts), and on a trigger character
 *    Monaco consults only the providers that declare it. Ctrl+Space is an Invoke, which also pulls
 *    in Monaco's word-based provider and dilutes the curated member list with words already in the
 *    buffer.
 *  - Nothing is clicked after the widget opens: Monaco hides it when the editor loses focus.
 *    expect() and screenshot() do not blur, locator.click() does.
 *
 * page.screenshot(), not an element shot: editorOptions sets `fixedOverflowWidgets`, so the widget
 * lives in a body-level overflow container that an element clip would cut away.
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

/** Caret lands after the first `v.`; everything in TAIL trails the completion list. */
const HEAD = "return (v) => v.";
const TAIL = 'GetOutDegree() >= 2 && v.TryGetProperty(out int age, "age") && age > 30;';

test("capture the delegate editor with IntelliSense open on a VertexFilter slot", async ({
  page,
  request,
}) => {
  // The modal is 1024px wide (w-5xl) and 80vh tall, so the viewport height decides how much empty
  // editor pane sits below the suggest list. 700 is the floor that still fits the sidebar without
  // scrolling it (snippets plus the NL panel at rest measure ~500px against a 560px modal).
  await page.setViewportSize({ width: 1440, height: 700 });

  // A real dataset behind the modal, so the top bar carries counts rather than zeroes.
  const jsonl = readFileSync(
    path.resolve(process.cwd(), "../samples/attack-surface.jsonl"),
    "utf8",
  );
  await request.head("/tabularasa/all", { headers: AUTH });
  expect((await request.post("/bulk/import", { headers: NDJSON, data: jsonl })).ok()).toBeTruthy();

  await registerSecuredInstance(page);

  // The slots live behind the advanced toggle, which is collapsed for an empty draft. They sit on
  // the Traverse screen's Path finding tab (feature studio-traverse-merge), entered by deep link
  // so the frame never depends on a remembered tab.
  await page.goto("/q/default/traverse?tab=path");
  await page.getByTestId("path-from").fill("1");
  await page.getByTestId("path-to").fill("42");
  await page.getByTestId("toggle-advanced").click();
  await page.getByTestId("slot-filter-vertexfilter").click();

  const editor = page.locator(".monaco-editor").first();
  await expect(editor).toBeVisible();
  await editor.click();
  await page.keyboard.press("Control+a");
  await page.keyboard.type(HEAD + TAIL, { delay: 6 });
  // The real POST /delegates/validate accepts this text before the widget is ever opened, so the
  // green badge in the frame is the server's verdict on exactly what is on screen.
  await expect(page.getByTestId("validation-valid")).toBeVisible({ timeout: 20_000 });

  // Walk back to just after the first dot, then re-fire the trigger character.
  for (let i = 0; i < TAIL.length; i++) await page.keyboard.press("ArrowLeft");
  await page.keyboard.press("Backspace");
  await page.keyboard.type(".");

  await expect(page.locator(".suggest-widget")).toHaveClass(/visible/, { timeout: 5_000 });
  // A member that is NOT a word in the buffer: proof this is the type model's list for VertexModel
  // and not Monaco's word-based fallback, which can only offer words already typed.
  await expect(
    page.locator(".suggest-widget .monaco-list-row", { hasText: "AnyPropertyValueMatches" }),
  ).toBeVisible();
  // Re-typing the dot restarts the 600ms validation debounce (DelegateEditor.tsx), and until it
  // fires the PRE-EDIT badge is still mounted and Use fragment still enabled. Asserting green here
  // alone would pass instantly on that stale badge, and the shot could still catch "validating…".
  // Sit out the debounce first, then require green.
  await page.waitForTimeout(900);
  await expect(page.getByTestId("validation-valid")).toBeVisible({ timeout: 20_000 });
  await expect(page.getByTestId("commit-fragment")).toBeEnabled();

  // Crop to the top bar plus the modal. Left uncropped, a 700px frame lets a half-cut slice of
  // the Path finding tab underneath peek out below the modal, which reads as a rendering glitch; a
  // taller frame hides it behind the modal but pays for that with an empty editor pane. `clip` is
  // a viewport crop, so the suggest widget (which lives in a body-level overflow container) is
  // still in the shot, unlike an element screenshot.
  const dialog = await page.locator('[role="dialog"]').boundingBox();
  if (!dialog) throw new Error("the delegate editor dialog has no box");
  await page.screenshot({
    path: "../docs/src/assets/images/screen-delegate-editor.png",
    clip: { x: 0, y: 0, width: 1440, height: Math.ceil(dialog.y + dialog.height + 22) },
  });
});
