// MIT License
//
// embed-smoke.spec.ts
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

// The library artifact's viability check, against the BUILT package consumed by a real
// bundler through the exports map (see e2e-embed/host/). It exercises exactly what the
// vitest suite mocks away and no unit can see: the monaco worker surviving lib-mode
// bundling, sigma rendering from the artifact, the scoped stylesheet neither leaking into
// the host page nor missing inside the embed, and a clean unmount.
//
// TWO tests, and the split is load-bearing rather than cosmetic. Everything that needs a
// monaco editor lives in the first, which never asserts JS-error cleanliness after disposing
// it; the unmount test runs on a page that never created an editor, so it can assert errors
// strictly across teardown. That arrangement, not a message filter, is what makes this suite
// deterministic: a third party's dispose path is never inside an error assertion's window.

import { expect, test, type Page } from "@playwright/test";
import type { StatusREST } from "../src/api/types";

/**
 * A truthful minimal /status so the shell reads "connected" without a live database.
 * `satisfies` ties the stub to the real wire type (the import is type-only, so playwright's
 * transpile keeps working); note e2e directories sit outside the tsc programs, so the
 * annotation is IDE-enforced, not CI-enforced - the pre-existing e2e/ convention.
 */
const STATUS_STUB = {
  vertexCount: 0,
  edgeCount: 0,
  usedMemory: 0,
  availableIndexPlugins: [],
  availablePathPlugins: [],
  availableAnalyticsPlugins: [],
  availableServicePlugins: [],
  apiKeyRequired: false,
} satisfies Partial<StatusREST>;

/**
 * JS exceptions always fail the smoke; console errors fail unless they are the resource-load
 * noise of the endpoints this fixture deliberately leaves unserved (/ns rides the tested
 * pre-namespace degradation path; screens fetch and render their error states).
 *
 * No message filter, on purpose: nothing here is allowed to be "expected". The one error that
 * once needed one is gone at the source - monaco 0.52 leaked a `CancellationError`
 * ("Canceled") as an unhandled rejection when the editor was disposed within 50 ms of a cursor
 * move, and `occurrencesHighlight: "off"` (src/delegate/editorOptions.ts) stops that promise
 * from ever being created. Belt as well as braces: no test below asserts error cleanliness on
 * the far side of a monaco disposal, because monaco's teardown is not ours to guarantee.
 */
function watchForErrors(page: Page): () => string[] {
  const errors: string[] = [];
  // The stack, not just the message: the one CI flake this suite ever had (run 31668695976)
  // was diagnosable only after a local reproduction, because the log recorded a bare
  // "Canceled" and named no frame.
  page.on("pageerror", (error) => {
    errors.push(`pageerror: ${error.message}\n${error.stack ?? "(no stack)"}`);
  });
  page.on("console", (message) => {
    if (message.type() === "error" && !/Failed to load resource/.test(message.text())) {
      errors.push(`console: ${message.text()}`);
    }
  });
  return () => errors;
}

test("the artifact mounts, scopes its styles, and runs monaco and sigma", async ({ page }) => {
  const errorsSoFar = watchForErrors(page);
  await page.route("**/status", (route) => route.fulfill({ json: STATUS_STUB }));

  await page.goto("/");

  // 1. The embed mounted inside the host region; the host chrome is untouched around it.
  const studioRoot = page.getByTestId("f8-studio-root");
  await expect(studioRoot).toBeVisible();
  await expect(page.locator("#host-heading")).toBeVisible();

  // 2. The standalone canvas component rendered from the same artifact (sigma WebGL) at
  //    load, and loading crashed nothing - checked here so a load-time failure names
  //    itself instead of surfacing as a missing element five steps later.
  await expect(page.locator("#canvas-region canvas").first()).toBeAttached();
  expect(errorsSoFar()).toEqual([]);

  // 3. Scoped styles: the host body keeps its own background (Studio's dark ink must not
  //    leak out), and the host's generic ".panel" keeps the host's dashed border while the
  //    embed styles its own subtree. Plain assertions: step 2 already awaited the loaded
  //    page, so the values are settled, and a real leak should fail fast, not after a poll
  //    timeout.
  expect(
    await page.evaluate(() => getComputedStyle(document.body).backgroundColor),
  ).toBe("rgb(255, 255, 255)");
  expect(
    await page.evaluate(() => getComputedStyle(document.querySelector("#host-panel")!).borderTopStyle),
  ).toBe("dashed");

  // 4. The theme token override landed on the scope root (the seam a host reskins through).
  expect(
    await page.evaluate(() => {
      const root = document.querySelector('[data-testid="f8-studio-root"]')!;
      return getComputedStyle(root).getPropertyValue("--color-accent").trim();
    }),
  ).toBe("#e2001a");

  // 5. The inlined asset rendered (a root-absolute URL would 404 against the host origin).
  const logo = page.getByAltText("F8 Studio");
  await expect(logo).toBeVisible();
  expect(await logo.evaluate((img: HTMLImageElement) => img.naturalWidth)).toBeGreaterThan(0);

  // 6. Monaco boots from the artifact: the Path screen's vertex-filter slot opens the
  //    delegate editor, whose worker is inlined by the lib build (vite cannot emit a
  //    separately served worker asset there). The WORKER itself is asserted - editor DOM
  //    alone proves nothing about the worker, and a failed worker-script fetch would hide
  //    inside the resource-noise filter above. A DELTA, not a count: the canvas already runs
  //    a graphology-layout worker at load, so a bare "more than zero" would pass with
  //    monaco's worker missing entirely. Typing is what a real user does and gets the lazy
  //    worker started soonest.
  await page.getByTestId("nav-path").click();

  // Landing on a namespace-scoped screen is the last precondition the first-run walkthrough's
  // auto path waits for (connected + arrived at an empty graph + never dismissed), and the
  // STATUS_STUB above reports an empty graph, so it opens HERE - modal, with a viewport-wide
  // scrim that owns every pointer event until it is closed. Dismissed the way an operator has
  // to, and ASSERTED rather than tolerated: if the auto-show ever stops firing on this path,
  // this step must fail loudly instead of quietly skipping a precondition it no longer has.
  await expect(page.getByTestId("first-run-overlay")).toBeVisible();
  await page.getByTestId("first-run-overlay-close").click();
  await expect(page.getByTestId("first-run-overlay")).toHaveCount(0);

  // Counted with the walkthrough gone, so the delta asserted below is monaco's worker and
  // nothing the intervening UI happened to start.
  const workersBeforeEditor = page.workers().length;
  await page.getByTestId("toggle-advanced").click();
  await page.getByTestId("slot-filter-vertexfilter").click();
  await expect(page.locator(".monaco-editor").first()).toBeVisible({ timeout: 15_000 });
  await page.locator(".monaco-editor").first().click();
  await page.keyboard.type("return (v) => true;", { delay: 10 });
  await expect
    .poll(() => page.workers().length, { timeout: 15_000 })
    .toBeGreaterThan(workersBeforeEditor);
  expect(
    page.workers().every((w) => w.url().startsWith("blob:")),
    `inlined workers must be blob: URLs, got: ${page.workers().map((w) => w.url()).join(", ")}`,
  ).toBe(true);

  // 7. No JS exception and no unexpected console error anywhere above. This runs BEFORE the
  //    editor is closed, on purpose: closing it disposes a THIRD-PARTY editor, and whether
  //    monaco's teardown is quiet is monaco's business, not this artifact's (0.52 leaked a
  //    cancellation as an unhandled rejection - microsoft/monaco-editor#4702 - which
  //    editorOptions.ts defuses at the source, but a version bump could reintroduce
  //    something like it). Asserting here makes cleanliness an ordering FACT in this file
  //    rather than a bet on a third party's dispose path.
  expect(errorsSoFar()).toEqual([]);

  // 8. Cancel removes the editor from the DOM. Last, and DOM-only: nothing about JS errors is
  //    asserted after this line. Unmount cleanliness is its own test below, on a page where
  //    no monaco editor was ever created, so that it CAN assert errors strictly.
  await page.getByRole("button", { name: "Cancel" }).click();
  await expect(page.locator(".monaco-editor")).toHaveCount(0);
});

/**
 * Teardown, on a page that never opened the delegate editor. That is the whole point: no
 * monaco editor exists to dispose, so this is the one place the smoke can assert BOTH the DOM
 * outcome and JS-error cleanliness across an unmount without racing a third party. What it
 * protects: a react unmount crash, a sigma/WebGL kill() throw, a change-feed EventSource close
 * throw. This suite is the only one in the repo that watches page errors at all (e2e/ installs
 * no listener), so this assertion is the only thing that can turn such a defect into a red
 * build.
 */
test("the artifact unmounts clean, leaving the host region empty", async ({ page }) => {
  const errorsSoFar = watchForErrors(page);
  await page.route("**/status", (route) => route.fulfill({ json: STATUS_STUB }));

  await page.goto("/");

  // Both embeds live before tearing down, so the unmount disposes a real sigma renderer and a
  // live query client rather than an empty shell.
  const studioRoot = page.getByTestId("f8-studio-root");
  await expect(studioRoot).toBeVisible();
  await expect(page.locator("#canvas-region canvas").first()).toBeAttached();

  await page.locator("#host-unmount").click();
  await expect(studioRoot).toHaveCount(0);
  expect(await page.locator("#studio-region").innerHTML()).toBe("");

  // One frame, so a rejection raised during teardown has reached the browser's task queue and
  // been reported to CDP before this assertion reads the list (mutation-checked: a throwing
  // unmount cleanup fails HERE, not at an earlier DOM step).
  await page.evaluate(() => new Promise((resolve) => requestAnimationFrame(() => resolve(null))));
  expect(errorsSoFar()).toEqual([]);
});
