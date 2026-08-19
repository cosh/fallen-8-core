// MIT License
//
// canvas2d-select.test.tsx
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
import { render } from "@testing-library/react";
import { DEFAULT_STYLE_CONFIG } from "../src/canvas/styleConfig";
import { EMPTY_OVERLAY, resolveStyles } from "../src/canvas/styleEngine";
import type { CanvasEdge, CanvasNode } from "../src/state/instanceStore";
import type { ElementRef } from "../src/canvas/GraphCanvas";
import { sigmaInstances } from "./fakeSigma";

/**
 * Pins the council-found blocker: Canvas2D registers Sigma click handlers once (mount
 * effect), so they MUST read the current onSelect through a ref — a frozen closure makes
 * upstream same-id navigation guards compare against a stale element, killing hop-back
 * clicks. Sigma itself needs WebGL, so a fake captures the handlers.
 */

vi.mock("sigma", () => import("./fakeSigma").then((m) => ({ default: m.FakeSigma })));
vi.mock("sigma/rendering", () => import("./fakeSigma").then((m) => m.sigmaRenderingModule));
vi.mock("@sigma/node-image", () => import("./fakeSigma").then((m) => m.sigmaNodeImageModule));
vi.mock("@sigma/edge-curve", () => import("./fakeSigma").then((m) => m.sigmaEdgeCurveModule));
vi.mock("graphology-layout-forceatlas2/worker", () =>
  import("./fakeSigma").then((m) => m.fa2WorkerModule));
vi.mock("graphology-layout-forceatlas2", () => import("./fakeSigma").then((m) => m.fa2Module));

import { Canvas2D } from "../src/canvas/Canvas2D";

const NODES: Record<number, CanvasNode> = {
  1: { id: 1, label: "a" },
  2: { id: 2, label: "b" },
};
const EDGES: Record<number, CanvasEdge> = {
  10: { id: 10, source: 1, target: 2, edgePropertyId: "knows", label: "knows" },
};

function renderCanvas(onSelect: (ref: ElementRef | null) => void) {
  const styles = resolveStyles(NODES, EDGES, EMPTY_OVERLAY, DEFAULT_STYLE_CONFIG);
  return render(
    <Canvas2D
      nodes={NODES}
      edges={EDGES}
      styles={styles}
      config={DEFAULT_STYLE_CONFIG}
      onSelect={onSelect}
    />,
  );
}

describe("Canvas2D selection handlers", () => {
  it("dispatches node, edge, and stage clicks to the LATEST onSelect, not the mount-time one", () => {
    const first = vi.fn();
    const second = vi.fn();
    const { rerender } = renderCanvas(first);
    const sigma = sigmaInstances.at(-1)!;

    const styles = resolveStyles(NODES, EDGES, EMPTY_OVERLAY, DEFAULT_STYLE_CONFIG);
    rerender(
      <Canvas2D
        nodes={NODES}
        edges={EDGES}
        styles={styles}
        config={DEFAULT_STYLE_CONFIG}
        onSelect={second}
      />,
    );

    sigma.handlers.clickNode({ node: "1" });
    sigma.handlers.clickEdge({ edge: "e10" });
    sigma.handlers.clickStage({});

    expect(first).not.toHaveBeenCalled();
    expect(second).toHaveBeenNthCalledWith(1, { kind: "node", id: 1 });
    expect(second).toHaveBeenNthCalledWith(2, { kind: "edge", id: 10 });
    expect(second).toHaveBeenNthCalledWith(3, null);
  });
});
