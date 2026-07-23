import { describe, expect, it, vi } from "vitest";
import { render } from "@testing-library/react";
import { DEFAULT_STYLE_CONFIG } from "../src/canvas/styleConfig";
import { EMPTY_OVERLAY, resolveStyles } from "../src/canvas/styleEngine";
import type { CanvasEdge, CanvasNode } from "../src/state/instanceStore";
import type { ElementRef } from "../src/canvas/GraphCanvas";

/**
 * Pins the council-found blocker: Canvas2D registers Sigma click handlers once (mount
 * effect), so they MUST read the current onSelect through a ref — a frozen closure makes
 * upstream same-id navigation guards compare against a stale element, killing hop-back
 * clicks. Sigma itself needs WebGL, so a fake captures the handlers.
 */

type Handler = (payload: { node?: string; edge?: string }) => void;
const sigmaInstances: { handlers: Record<string, Handler>; killed: boolean }[] = [];

vi.mock("sigma", () => ({
  default: class FakeSigma {
    handlers: Record<string, Handler> = {};
    killed = false;
    constructor() {
      sigmaInstances.push(this);
    }
    on(event: string, handler: Handler) {
      this.handlers[event] = handler;
    }
    refresh() {}
    kill() {
      this.killed = true;
    }
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
  // Singleton edges: no parallel index (straight rendering path).
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
