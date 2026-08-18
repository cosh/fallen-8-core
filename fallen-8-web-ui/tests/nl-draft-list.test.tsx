// MIT License
//
// nl-draft-list.test.tsx
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

import { describe, expect, it, vi } from "vitest";
import { render, screen, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { NlDraftList, type NlDraftView } from "../src/delegate/nl/NlDraftList";

/**
 * The shared NL-assist draft list (feature nl-assist-draft-review-ux): the single home for the
 * three review affordances both panels now inherit — newest draft on top, a scrollable region,
 * and unrated drafts flagged until judged — with generation-order numbering and indices intact.
 */

const view = (over: Partial<NlDraftView> = {}): NlDraftView => ({
  valid: true,
  verdict: null,
  active: false,
  loadTitle: "load",
  ...over,
});

function renderList(drafts: NlDraftView[], onLoad = vi.fn(), onRate = vi.fn()) {
  render(
    <NlDraftList
      testid="drafts"
      verdictTestidPrefix="v"
      drafts={drafts}
      onLoad={onLoad}
      onRate={onRate}
    />,
  );
  return { onLoad, onRate };
}

describe("NlDraftList", () => {
  it("renders the newest draft on top while keeping generation-order numbering", () => {
    renderList([view(), view(), view({ active: true })]);

    const items = within(screen.getByTestId("drafts")).getAllByRole("listitem");
    expect(items).toHaveLength(3);
    // Generated 1→2→3; displayed 3→2→1 so the freshest (and in-editor) draft leads.
    expect(items[0]).toHaveTextContent("draft 3");
    expect(items[0]).toHaveTextContent("(in editor)");
    expect(items[1]).toHaveTextContent("draft 2");
    expect(items[2]).toHaveTextContent("draft 1");
  });

  it("flags only unrated drafts with data-unjudged", () => {
    // Generated: [null, up, down]; displayed reversed.
    renderList([view({ verdict: null }), view({ verdict: "up" }), view({ verdict: "down" })]);

    const items = within(screen.getByTestId("drafts")).getAllByRole("listitem");
    expect(items[0]).not.toHaveAttribute("data-unjudged"); // draft 3 (down)
    expect(items[1]).not.toHaveAttribute("data-unjudged"); // draft 2 (up)
    expect(items[2]).toHaveAttribute("data-unjudged", "true"); // draft 1 (unrated)
  });

  it("calls load/rate with the original generation index, not the display position", async () => {
    const user = userEvent.setup();
    const { onLoad, onRate } = renderList([view(), view(), view()]);

    // Top row is the newest draft = original index 2.
    await user.click(screen.getByRole("button", { name: /draft 3/ }));
    expect(onLoad).toHaveBeenCalledWith(2);

    await user.click(within(screen.getByTestId("v-2")).getByRole("button", { name: "👍" }));
    expect(onRate).toHaveBeenCalledWith(2, "up");

    // Bottom row is the oldest = original index 0.
    await user.click(within(screen.getByTestId("v-0")).getByRole("button", { name: "👎" }));
    expect(onRate).toHaveBeenCalledWith(0, "down");
  });

  it("gives the list its own bounded vertical scroll region", () => {
    renderList([view()]);
    const list = screen.getByTestId("drafts");
    expect(list.className).toContain("overflow-y-auto");
    expect(list.className).toContain("max-h-64");
  });

  it("renders the host-supplied label suffix and below slot", () => {
    renderList([
      view({
        valid: false,
        labelSuffix: " (invalid)",
        below: <span>raw stats here</span>,
      }),
    ]);

    expect(screen.getByRole("button", { name: /draft 1 \(invalid\)/ })).toBeInTheDocument();
    expect(screen.getByText("raw stats here")).toBeInTheDocument();
    // Invalid draft shows the ✗ marker, not the ✓ tick.
    expect(screen.getByTestId("drafts")).toHaveTextContent("✗");
  });
});
