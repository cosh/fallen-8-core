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
 */
function watchForErrors(page: Page): () => string[] {
  const errors: string[] = [];
  page.on("pageerror", (error) => errors.push(`pageerror: ${error.message}`));
  page.on("console", (message) => {
    if (message.type() === "error" && !/Failed to load resource/.test(message.text())) {
      errors.push(`console: ${message.text()}`);
    }
  });
  return () => errors;
}

test("the artifact mounts, scopes its styles, runs monaco and sigma, and unmounts clean", async ({
  page,
}) => {
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
  //    inside the resource-noise filter above. Typing gives the lazy worker a reason to
  //    start. Closed again so the modal does not block the unmount click.
  await page.getByTestId("nav-path").click();
  await page.getByTestId("toggle-advanced").click();
  await page.getByTestId("slot-filter-vertexfilter").click();
  await expect(page.locator(".monaco-editor").first()).toBeVisible({ timeout: 15_000 });
  await page.locator(".monaco-editor").first().click();
  await page.keyboard.type("return (v) => true;", { delay: 10 });
  await expect
    .poll(() => page.workers().length, { timeout: 15_000 })
    .toBeGreaterThan(0);
  expect(
    page.workers().every((w) => w.url().startsWith("blob:")),
    `inlined workers must be blob: URLs, got: ${page.workers().map((w) => w.url()).join(", ")}`,
  ).toBe(true);
  await page.getByRole("button", { name: "Cancel" }).click();
  await expect(page.locator(".monaco-editor")).toHaveCount(0);

  // 7. No JS exception and no unexpected console error anywhere above.
  expect(errorsSoFar()).toEqual([]);

  // 8. Unmount leaves the host region empty (no orphaned portals, no leftover DOM).
  await page.locator("#host-unmount").click();
  await expect(studioRoot).toHaveCount(0);
  expect(await page.locator("#studio-region").innerHTML()).toBe("");
});
