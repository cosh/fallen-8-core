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

import { createRef } from "react";
import { describe, expect, it, vi } from "vitest";
import { render } from "@testing-library/react";
import type { CanvasEdge, CanvasNode } from "../src/state/instanceStore";
import type { F8GraphCanvasHandle } from "../src/canvas/GraphCanvas";
import { sigmaInstances } from "./fakeSigma";
import { liveResizeObservers, resizeObserved } from "./resizeObserver";

/**
 * The component-level embed (feature studio-embeddable): F8GraphCanvas renders Studio's
 * graph canvas from literal data on a page that never mounted the app shell - its own
 * .f8-studio scope root, optional theme vars, defaults for everything but the data, and
 * selection callbacks wired through. Sigma needs WebGL, so the same fake the Canvas2D
 * tests use captures the handlers.
 */

vi.mock("sigma", () => import("./fakeSigma").then((m) => ({ default: m.FakeSigma })));
vi.mock("sigma/rendering", () => import("./fakeSigma").then((m) => m.sigmaRenderingModule));
vi.mock("@sigma/node-image", () => import("./fakeSigma").then((m) => m.sigmaNodeImageModule));
vi.mock("@sigma/edge-curve", () => import("./fakeSigma").then((m) => m.sigmaEdgeCurveModule));
vi.mock("graphology-layout-forceatlas2/worker", () =>
  import("./fakeSigma").then((m) => m.fa2WorkerModule));
vi.mock("graphology-layout-forceatlas2", () => import("./fakeSigma").then((m) => m.fa2Module));

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

  it("exposes a camera handle that survives the renderer being swapped underneath", () => {
    // The handle delegates rather than handing out the renderer's own object, so switching
    // config.renderer cannot leave a host holding a dead one.
    const ref = createRef<F8GraphCanvasHandle>();
    const { unmount } = render(<F8GraphCanvas ref={ref} nodes={NODES} edges={EDGES} />);

    expect(ref.current?.getCameraRatio()).toBe(1);
    ref.current!.setCameraRatio(0.4);
    expect(sigmaInstances.at(-1)!.camera.ratio).toBe(0.4);


    // A host that STORED the handle (a callback closing over it, say) and calls it after the canvas
    // is gone must get a no-op, not a crash. React nulls ref.current on unmount, so the stored
    // object is the only way to reach the delegation in that state.
    const stored = ref.current!;
    unmount();
    expect(ref.current).toBeNull();
    expect(() => stored.fitToView()).not.toThrow();
    expect(() => stored.setCameraRatio(2)).not.toThrow();
    expect(stored.getCameraRatio()).toBe(1); // "fits", the honest answer with nothing mounted
  });

  it("survives a host reflow without moving the visitor's camera", () => {
    // The wrapper deliberately does NOT re-fit on resize. It used to, gated on the camera ratio
    // still being 1, which read as "nobody has touched it" and was wrong: sigma's drag handlers
    // write x and y and never touch the ratio, so a visitor who had merely PANNED was silently
    // yanked back to centre on the host's next reflow. Re-framing is the renderer's job now.
    render(<F8GraphCanvas nodes={NODES} edges={EDGES} />);
    const sigma = sigmaInstances.at(-1)!;

    // A visitor pans: sigma moves x and y and leaves the ratio at 1.
    sigma.camera.x = 0.92;
    sigma.camera.y = 0.11;
    const refreshesBefore = sigma.scheduledRefreshCount;

    resizeObserved();

    expect(sigma.camera.animations).toEqual([]);
    expect(sigma.camera.x).toBe(0.92);
    expect(sigma.camera.y).toBe(0.11);
    // Re-framed all the same: the renderer re-measures and repaints without touching the camera.
    expect(sigma.scheduledRefreshCount).toBe(refreshesBefore + 1);
  });

  it("leaves no observer behind on unmount", () => {
    const { unmount } = render(<F8GraphCanvas nodes={NODES} edges={EDGES} />);
    const sigma = sigmaInstances.at(-1)!;
    expect(liveResizeObservers()).toBeGreaterThan(0);
    unmount();

    expect(liveResizeObservers()).toBe(0);
    const after = sigma.scheduledRefreshCount;
    resizeObserved();
    expect(sigma.scheduledRefreshCount).toBe(after);
  });
});
