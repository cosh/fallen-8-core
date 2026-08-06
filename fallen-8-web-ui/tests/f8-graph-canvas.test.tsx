// MIT License
//
// f8-graph-canvas.test.tsx
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
import type { CanvasEdge, CanvasNode } from "../src/state/instanceStore";

/**
 * The component-level embed (feature studio-embeddable): F8GraphCanvas renders Studio's
 * graph canvas from literal data on a page that never mounted the app shell - its own
 * .f8-studio scope root, optional theme vars, defaults for everything but the data, and
 * selection callbacks wired through. Sigma needs WebGL, so the same fake the Canvas2D
 * tests use captures the handlers.
 */

type Handler = (payload: { node?: string; edge?: string }) => void;
const sigmaInstances: { handlers: Record<string, Handler> }[] = [];

vi.mock("sigma", () => ({
  default: class FakeSigma {
    handlers: Record<string, Handler> = {};
    constructor() {
      sigmaInstances.push(this);
    }
    on(event: string, handler: Handler) {
      this.handlers[event] = handler;
    }
    refresh() {}
    kill() {}
  },
}));
vi.mock("sigma/rendering", () => ({
  EdgeArrowProgram: class {},
  EdgeRectangleProgram: class {},
  NodeCircleProgram: class {},
}));
vi.mock("@sigma/node-image", () => ({ createNodeImageProgram: () => class {} }));
vi.mock("@sigma/edge-curve", () => ({
  default: class {},
  EdgeCurvedArrowProgram: class {},
  DEFAULT_EDGE_CURVATURE: 0.25,
  indexParallelEdgesIndex: (graph: {
    forEachEdge: (cb: (edge: string) => void) => void;
    setEdgeAttribute: (edge: string, name: string, value: unknown) => void;
  }) => {
    graph.forEachEdge((edge) => {
      graph.setEdgeAttribute(edge, "parallelIndex", null);
      graph.setEdgeAttribute(edge, "parallelMaxIndex", null);
    });
  },
}));
vi.mock("graphology-layout-forceatlas2/worker", () => ({
  default: class {
    start() {}
    stop() {}
    kill() {}
  },
}));
vi.mock("graphology-layout-forceatlas2", () => ({
  default: { inferSettings: () => ({}) },
}));

import { F8GraphCanvas } from "../src/embed/F8GraphCanvas";

const NODES: Record<number, CanvasNode> = {
  1: { id: 1, label: "turbine" },
  2: { id: 2, label: "report" },
};
const EDGES: Record<number, CanvasEdge> = {
  10: { id: 10, source: 1, target: 2, edgePropertyId: "describes", label: "describes" },
};

describe("F8GraphCanvas", () => {
  it("renders from literal data inside its own .f8-studio scope with theme vars", () => {
    const { container } = render(
      <F8GraphCanvas nodes={NODES} edges={EDGES} theme={{ accent: "#e2001a" }} />,
    );

    const root = container.querySelector(".f8-studio") as HTMLElement;
    expect(root).not.toBeNull();
    expect(root.style.getPropertyValue("--color-accent")).toBe("#e2001a");
    expect(sigmaInstances.length).toBeGreaterThan(0);
  });

  it("dispatches selection to the host callback (and survives having none)", () => {
    const onSelect = vi.fn();
    render(<F8GraphCanvas nodes={NODES} edges={EDGES} onSelect={onSelect} />);

    const sigma = sigmaInstances.at(-1)!;
    sigma.handlers.clickNode({ node: "1" });
    expect(onSelect).toHaveBeenCalledWith({ kind: "node", id: 1 });
    sigma.handlers.clickStage({});
    expect(onSelect).toHaveBeenCalledWith(null);

    // No onSelect prop: clicks are a no-op, not a crash.
    render(<F8GraphCanvas nodes={NODES} edges={EDGES} />);
    expect(() => sigmaInstances.at(-1)!.handlers.clickNode({ node: "1" })).not.toThrow();
  });
});
