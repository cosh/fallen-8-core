// MIT License
//
// firstRun.ts
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

import type { Page } from "@playwright/test";

/**
 * Close the first-run walkthrough if it opened itself (feature studio-first-run).
 *
 * The show is shell-level: on an EMPTY namespace it opens as a modal over whatever screen is
 * showing, in a fresh browser profile, which is exactly the state every e2e run starts in. A spec
 * that then drives the UI would be clicking at a scrim, and a capture would photograph one.
 *
 * Call it after the instance is connected (the show needs a /status answer to know the graph is
 * empty) and after anything that empties the namespace, e.g. `HEAD /tabularasa/all` - it re-arms
 * there. A no-op with a populated graph or an unconnected instance, at the cost of the wait.
 *
 * Closing it records the dismissal for that namespace, which is the point: it stays closed.
 */
export async function closeIntroIfOpen(page: Page, timeoutMs = 4_000): Promise<void> {
  const overlay = page.getByTestId("first-run-overlay");
  try {
    await overlay.waitFor({ state: "visible", timeout: timeoutMs });
  } catch {
    return; // never opened (populated graph, or not connected) - nothing to close
  }
  await page.getByTestId("first-run-overlay-close").click();
  await overlay.waitFor({ state: "hidden" });
}
