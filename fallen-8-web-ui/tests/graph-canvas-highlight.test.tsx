// MIT License
//
// graph-canvas-highlight.test.tsx
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
import { render, screen } from "@testing-library/react";
import { DEFAULT_STYLE_CONFIG } from "../src/canvas/styleConfig";
import type { ElementRef } from "../src/canvas/GraphCanvas";

/**
 * The REAL GraphCanvas derives highlightId = (highlight?.kind === "node") ? id : null and hands
 * only that to the renderers, so an edge never reaches Canvas2D's getNodeDisplayData(id) (node and
 * edge ids share one numeric space). The Canvas mocks capture that derived prop so the node-only
 * rule is verified without a WebGL context.
 */

const captured: (number | null | undefined)[] = [];

vi.mock("../src/canvas/Canvas2D", () => ({
  Canvas2D: ({ highlightId }: { highlightId?: number | null }) => {
    captured.push(highlightId);
    return <div data-testid="c2d" data-hid={highlightId ?? "null"} />;
  },
}));

import { GraphCanvas } from "../src/canvas/GraphCanvas";

function renderWith(highlight: ElementRef | null) {
  return render(
    <GraphCanvas
      nodes={{ 12: { id: 12, label: "a" }, 50: { id: 50, label: "b" } }}
      edges={{}}
      // Force the 2D renderer so the (non-lazy) mocked Canvas2D mounts.
      config={{ ...DEFAULT_STYLE_CONFIG, renderer: "2d" }}
      pathOverlay={null}
      highlight={highlight}
      onSelect={() => {}}
    />,
  );
}

describe("GraphCanvas highlight derivation", () => {
  it("passes a node highlight's id to the renderer", () => {
    renderWith({ kind: "node", id: 12 });
    expect(screen.getByTestId("c2d")).toHaveAttribute("data-hid", "12");
  });

  it("collapses an edge highlight to null (edges never spotlight a node)", () => {
    renderWith({ kind: "edge", id: 50 });
    expect(screen.getByTestId("c2d")).toHaveAttribute("data-hid", "null");
  });

  it("passes null when there is no highlight", () => {
    renderWith(null);
    expect(screen.getByTestId("c2d")).toHaveAttribute("data-hid", "null");
  });
});
