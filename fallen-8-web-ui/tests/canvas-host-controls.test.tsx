// MIT License
//
// canvas-host-controls.test.tsx
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
import { DEFAULT_STYLE_CONFIG, type StyleConfig } from "../src/canvas/styleConfig";
import { EMPTY_OVERLAY, resolveStyles } from "../src/canvas/styleEngine";
import { FIT_DURATION_MS, type F8GraphCanvasHandle } from "../src/canvas/GraphCanvas";
import { fitCameraRatio } from "../src/canvas/eclipse";
import type { CanvasEdge, CanvasNode } from "../src/state/instanceStore";
import { liveResizeObservers, resizeObserved } from "./resizeObserver";
import { sigmaInstances } from "./fakeSigma";
import { forceGraphInstances } from "./fakeForceGraph";

/**
 * The host-facing canvas controls (feature canvas-host-controls) as the 2D renderer actually wires
 * them: label sizes reaching Sigma's settings and staying live, the imperative camera handle, and
 * the container observer. All three are invisible to the pure geometry tests in eclipse.test.ts,
 * which is where the arithmetic itself is pinned.
 *
 * Both renderers are exercised. The 3D half deliberately does NOT re-assert the geometry that
 * eclipse.test.ts already pins; it covers the WIRING, above all the one invariant that spans the two
 * renderers: a default fit must leave the camera ratio at 1. The 3D fake computes its fitted distance
 * from the library's own formula rather than from the code under test, so that assertion is a real
 * comparison and not a tautology.
 */

vi.mock("sigma", () => import("./fakeSigma").then((m) => ({ default: m.FakeSigma })));
vi.mock("sigma/rendering", () => import("./fakeSigma").then((m) => m.sigmaRenderingModule));
vi.mock("@sigma/node-image", () => import("./fakeSigma").then((m) => m.sigmaNodeImageModule));
vi.mock("@sigma/edge-curve", () => import("./fakeSigma").then((m) => m.sigmaEdgeCurveModule));
vi.mock("graphology-layout-forceatlas2/worker", () =>
  import("./fakeSigma").then((m) => m.fa2WorkerModule));
vi.mock("graphology-layout-forceatlas2", () => import("./fakeSigma").then((m) => m.fa2Module));
vi.mock("3d-force-graph", () =>
  import("./fakeForceGraph").then((m) => ({ default: m.FakeForceGraph })));

import { Canvas2D } from "../src/canvas/Canvas2D";
import { Canvas3D } from "../src/canvas/Canvas3D";

const NODES: Record<number, CanvasNode> = {
  1: { id: 1, label: "turbine" },
  2: { id: 2, label: "site" },
};
const EDGES: Record<number, CanvasEdge> = {
  10: { id: 10, source: 1, target: 2, edgePropertyId: "locatedAt", label: "locatedAt" },
};

function renderCanvas(patch: Partial<StyleConfig> = {}, ref?: React.Ref<F8GraphCanvasHandle>) {
  const before = sigmaInstances.length;
  const config = { ...DEFAULT_STYLE_CONFIG, ...patch };
  const view = render(
    <Canvas2D
      nodes={NODES}
      edges={EDGES}
      styles={resolveStyles(NODES, EDGES, EMPTY_OVERLAY, config)}
      config={config}
      onSelect={() => {}}
      ref={ref}
    />,
  );
  // Positional reads of a shared registry are how one test ends up asserting another's instance;
  // taking the one this render created, and failing loudly if it did not create exactly one, makes
  // any such leak a named failure instead of a puzzling value mismatch.
  const created = sigmaInstances.slice(before);
  expect(created).toHaveLength(1);
  return { view, sigma: created[0] };
}

describe("label sizes reach Sigma", () => {
  it("constructs with the documented defaults when nothing is configured", () => {
    const { sigma } = renderCanvas();
    expect(sigma.constructorSettings.labelSize).toBe(11);
    expect(sigma.constructorSettings.edgeLabelSize).toBe(9);
  });

  it("constructs with a configured size instead", () => {
    const { sigma } = renderCanvas({ labelSize: 22, edgeLabelSize: 18 });
    expect(sigma.constructorSettings.labelSize).toBe(22);
    expect(sigma.constructorSettings.edgeLabelSize).toBe(18);
  });

  it("does not re-apply on mount what the constructor was already given", () => {
    // setSettings re-validates the settings, rewrites the camera state and schedules a full
    // re-index. Paying that on mount to write the numbers Sigma was just built with is a second
    // O(V+E) pass for no visual change.
    const { sigma } = renderCanvas({ labelSize: 22 });
    expect(sigma.constructorSettings.labelSize).toBe(22);
    expect(sigma.settings).toEqual({});
  });

  it("applies a later change without a remount", () => {
    // Label size is a Sigma SETTING, not a per-element attribute, so the element sync effect cannot
    // carry it; a host raising it for a wide container must not have to tear the canvas down.
    const config = { ...DEFAULT_STYLE_CONFIG, labelSize: 12 };
    const view = render(
      <Canvas2D
        nodes={NODES}
        edges={EDGES}
        styles={resolveStyles(NODES, EDGES, EMPTY_OVERLAY, config)}
        config={config}
        onSelect={() => {}}
      />,
    );
    const sigma = sigmaInstances.at(-1)!;
    const instanceCount = sigmaInstances.length;

    const wider = { ...DEFAULT_STYLE_CONFIG, labelSize: 30, edgeLabelSize: 24 };
    view.rerender(
      <Canvas2D
        nodes={NODES}
        edges={EDGES}
        styles={resolveStyles(NODES, EDGES, EMPTY_OVERLAY, wider)}
        config={wider}
        onSelect={() => {}}
      />,
    );

    expect(sigmaInstances.length).toBe(instanceCount); // same Sigma, not a new one
    expect(sigma.settings.labelSize).toBe(30);
    expect(sigma.settings.edgeLabelSize).toBe(24);
    expect(sigma.killed).toBe(false);
  });
});

describe("the imperative camera handle", () => {
  it("is attached to the host's ref while mounted, and released on unmount", () => {
    const ref = createRef<F8GraphCanvasHandle>();
    const { view } = renderCanvas({}, ref);
    expect(typeof ref.current?.fitToView).toBe("function");
    view.unmount();
    expect(ref.current).toBeNull();
  });

  it("fits by resetting the camera to the frame sigma already uses", () => {
    const ref = createRef<F8GraphCanvasHandle>();
    const { sigma } = renderCanvas({}, ref);

    ref.current!.fitToView();

    // Re-measured first: getDimensions() is a cache, so a fit provoked BY a container change would
    // otherwise frame the box the graph just left.
    expect(sigma.resizeCount).toBeGreaterThan(0);
    const [animation] = sigma.camera.animations;
    expect(animation.state).toEqual({ x: 0.5, y: 0.5, ratio: 1, angle: 0 });
    expect(animation.options.duration).toBe(FIT_DURATION_MS);
    // Painting is scheduled explicitly: an unchanged camera state emits nothing, so a fit asking for
    // the frame already in effect would otherwise leave a freshly resized layer unpainted.
    expect(sigma.scheduledRenderCount).toBeGreaterThan(0);
  });

  it("resets the angle, because the projection rotates after the zoom divide", () => {
    // A rotated camera lets the corners of the bounding box back out of frame, so a fit that left
    // the angle alone would not actually show the whole graph.
    const ref = createRef<F8GraphCanvasHandle>();
    const { sigma } = renderCanvas({}, ref);
    sigma.camera.angle = 0.7;
    ref.current!.fitToView();
    expect(sigma.camera.angle).toBe(0);
  });

  it("zooms out for a padding wider than the stage, by the ratio the geometry defines", () => {
    const ref = createRef<F8GraphCanvasHandle>();
    const { sigma } = renderCanvas({}, ref);
    sigma.dimensions = { width: 1440, height: 900 };

    ref.current!.fitToView(300, 60);

    const [animation] = sigma.camera.animations;
    expect(animation.state.ratio).toBeCloseTo(fitCameraRatio({ width: 1440, height: 900 }, 30, 60), 12);
    expect(animation.state.ratio).toBeGreaterThan(1);
    expect(animation.options.duration).toBe(300);
  });

  it("frames the graph at 390, 1440 and 3840 px wide", () => {
    const ref = createRef<F8GraphCanvasHandle>();
    const { sigma } = renderCanvas({}, ref);

    for (const [width, height] of [
      [390, 844],
      [1440, 900],
      [3840, 2160],
    ]) {
      sigma.camera.animations.length = 0;
      sigma.dimensions = { width, height };
      ref.current!.fitToView(0, 60);
      const [animation] = sigma.camera.animations;
      // Whatever the box, the camera centres on the bounding box and the zoom stays sane.
      expect(animation.state.x).toBe(0.5);
      expect(animation.state.y).toBe(0.5);
      expect(animation.state.ratio).toBeGreaterThan(0);
      expect(Number.isFinite(animation.state.ratio)).toBe(true);
    }
  });

  it("never hands Sigma's tween a zero or non-finite duration", () => {
    // Sigma computes elapsed/duration with no guard, so 0 writes NaN into x, y and ratio for a frame
    // and blanks the canvas. A plain-JS host can pass either.
    const ref = createRef<F8GraphCanvasHandle>();
    const { sigma } = renderCanvas({}, ref);

    for (const duration of [0, -50, Number.NaN, undefined] as (number | undefined)[]) {
      sigma.camera.animations.length = 0;
      ref.current!.fitToView(duration);
      const { options, state } = sigma.camera.animations[0];
      expect(options.duration as number).toBeGreaterThanOrEqual(1);
      expect(Number.isFinite(state.ratio)).toBe(true);
    }
  });

  it("reads sigma's live camera ratio", () => {
    const ref = createRef<F8GraphCanvasHandle>();
    const { sigma } = renderCanvas({}, ref);
    sigma.camera.ratio = 2.5;
    expect(ref.current!.getCameraRatio()).toBe(2.5);
  });

  it("sets a usable camera ratio and refuses one that would blank the canvas", () => {
    const ref = createRef<F8GraphCanvasHandle>();
    const { sigma } = renderCanvas({}, ref);

    ref.current!.setCameraRatio(0.5);
    expect(sigma.camera.ratio).toBe(0.5);

    // 0, negative and NaN all divide element sizes and the projection; there is no recovering from
    // them by hand, so they are refused rather than clamped into something the host did not ask for.
    const writesAfterGoodValue = sigma.camera.animations.length;
    for (const bad of [0, -1, Number.NaN, Number.POSITIVE_INFINITY]) {
      ref.current!.setCameraRatio(bad);
    }
    expect(sigma.camera.animations.length).toBe(writesAfterGoodValue);
    expect(sigma.camera.ratio).toBe(0.5);
  });

  it("cannot be silently overwritten by a fit that is still animating", () => {
    // sigma's tween keeps its own start-state snapshot and an rAF loop that writes over any
    // setState, so a plain state write here would be swallowed and the camera would land on the
    // fit's ratio. Routing through animate() is what cancels the running tween.
    const ref = createRef<F8GraphCanvasHandle>();
    const { sigma } = renderCanvas({}, ref);

    ref.current!.fitToView(600, 200); // a long tween, deliberately still running
    const duringFit = sigma.camera.animations.length;
    ref.current!.setCameraRatio(0.25);

    expect(sigma.camera.animations.length).toBe(duringFit + 1);
    expect(sigma.camera.animations.at(-1)!.state).toEqual({ ratio: 0.25 });
    expect(sigma.camera.ratio).toBe(0.25);
  });
});

describe("the container observer", () => {
  it("reschedules a render when the box changes, and leaves the camera alone", () => {
    // Why a refresh and deliberately not a fit: see the observer in Canvas2D.tsx.
    const { sigma } = renderCanvas();
    const before = sigma.scheduledRefreshCount;
    sigma.camera.ratio = 3;

    resizeObserved();

    expect(sigma.scheduledRefreshCount).toBe(before + 1);
    expect(sigma.camera.ratio).toBe(3);
    expect(sigma.camera.animations).toEqual([]);
    expect(sigma.camera.stateWrites).toEqual([]);
  });

  it("stops observing on unmount", () => {
    const { view, sigma } = renderCanvas();
    expect(liveResizeObservers()).toBeGreaterThan(0);

    view.unmount();
    expect(liveResizeObservers()).toBe(0);

    // A leaked observer firing against a killed Sigma is exactly the crash this guards.
    const after = sigma.scheduledRefreshCount;
    resizeObserved();
    expect(sigma.scheduledRefreshCount).toBe(after);
  });
});


/**
 * Renders the 3D canvas, places its nodes (the force engine never runs here), and returns the fake
 * plus the handle. Placing the nodes matters: an unplaced cloud has no bounding box, so no fit is
 * defined and every ratio question answers 1 vacuously.
 */
function render3D(container: { width: number; height: number }, ref: React.Ref<F8GraphCanvasHandle>) {
  const before = forceGraphInstances.length;
  const config: StyleConfig = { ...DEFAULT_STYLE_CONFIG, renderer: "3d" };
  const view = render(
    <Canvas3D
      nodes={NODES}
      edges={EDGES}
      styles={resolveStyles(NODES, EDGES, EMPTY_OVERLAY, config)}
      config={config}
      onSelect={() => {}}
      ref={ref}
    />,
  );
  const created = forceGraphInstances.slice(before);
  expect(created).toHaveLength(1);
  const fg = created[0];

  // jsdom reports 0 for clientWidth/clientHeight, so the component's own measurement is stubbed to
  // the box under test; the fake keeps whatever the component pushes into it.
  const element = view.container.querySelector('[data-testid="graph-canvas"]') as HTMLElement;
  Object.defineProperty(element, "clientWidth", { value: container.width, configurable: true });
  Object.defineProperty(element, "clientHeight", { value: container.height, configurable: true });
  fg.widthPx = container.width;
  fg.heightPx = container.height;

  // Place the cloud. These are the very objects the component holds, so its own fit arithmetic and
  // the fake's both see them.
  const placed = fg.graphDataCalls.at(-1)!.nodes;
  expect(placed.length).toBeGreaterThan(0);
  placed.forEach((node, i) => {
    node.x = 40 * (i + 1);
    node.y = -25 * (i + 1);
    node.z = 12 * (i + 1);
  });
  return { view, fg };
}

describe("the 3D camera handle", () => {
  it("reports ratio 1 right after a default fit, at every container size", () => {
    // THE cross-renderer invariant. The fake parks the camera using three-render-objects' own
    // formula, so agreement here means the component's denominator really matches what the library
    // did, clamp included. 140 px is the case that catches a denominator computed from an inset the
    // fit could not perform: there the default 60 px is clamped, so an unclamped denominator would
    // report roughly 0.72 for a perfectly fitted graph.
    for (const box of [
      { width: 1440, height: 900 },
      { width: 390, height: 844 },
      { width: 3840, height: 2160 },
      { width: 1440, height: 140 },
    ]) {
      const ref = createRef<F8GraphCanvasHandle>();
      const { view } = render3D(box, ref);
      ref.current!.fitToView();
      expect(ref.current!.getCameraRatio()).toBeCloseTo(1, 6);
      view.unmount();
    }
  });

  it("clamps a fit inset that would send the camera to infinity", () => {
    // zoomToFit's padding is an unguarded divisor: at exactly half the height the fit distance is
    // Infinity and the library applies it, which parks the camera nowhere.
    const ref = createRef<F8GraphCanvasHandle>();
    const { fg } = render3D({ width: 1440, height: 400 }, ref);

    ref.current!.fitToView(0, 200); // exactly half the height
    const { padding } = fg.zoomToFitCalls.at(-1)!;
    expect(padding).toBeLessThan(200);
    expect(Number.isFinite(fg.position.z)).toBe(true);
    expect(Math.hypot(fg.position.x, fg.position.y, fg.position.z)).toBeGreaterThan(0);
  });

  it("re-measures the container before fitting, so a host can fit from its own layout handler", () => {
    // fg.width/height are library state that only this component's observer updates, so without the
    // re-measure a fit called straight after a host reflow would frame the box just left behind.
    const ref = createRef<F8GraphCanvasHandle>();
    const { view, fg } = render3D({ width: 800, height: 600 }, ref);

    const element = view.container.querySelector('[data-testid="graph-canvas"]') as HTMLElement;
    Object.defineProperty(element, "clientWidth", { value: 1600, configurable: true });
    ref.current!.fitToView(0);

    expect(fg.width()).toBe(1600);
  });

  it("re-fits on resize until the visitor takes the camera, then never again", () => {
    // Unlike 2D, a resize alone does not re-frame here: the fit distance depends on the aspect ratio
    // and on a field of view measured against height, and a resize changes both.
    const ref = createRef<F8GraphCanvasHandle>();
    const { fg } = render3D({ width: 1440, height: 900 }, ref);
    const fitsAfterMount = fg.zoomToFitCalls.length;

    resizeObserved();
    expect(fg.zoomToFitCalls.length).toBe(fitsAfterMount + 1);

    fg.emitControls("start"); // the visitor grabs the orbit controls
    resizeObserved();
    resizeObserved();
    expect(fg.zoomToFitCalls.length).toBe(fitsAfterMount + 1);
  });

  it("sets a usable ratio by re-aiming at the origin, and refuses one that would blank the scene", () => {
    const ref = createRef<F8GraphCanvasHandle>();
    const { fg } = render3D({ width: 1440, height: 900 }, ref);
    ref.current!.fitToView(0);
    const fitted = Math.hypot(fg.position.x, fg.position.y, fg.position.z);

    ref.current!.setCameraRatio(2);
    expect(Math.hypot(fg.position.x, fg.position.y, fg.position.z)).toBeCloseTo(2 * fitted, 4);
    // A fit re-aims at the graph origin, so a zoom has to as well or "ratio 1 fits" stops holding.
    expect(fg.lookAt).toEqual({ x: 0, y: 0, z: 0 });
    // Round trip: reading the ratio back and setting it again must not drift.
    expect(ref.current!.getCameraRatio()).toBeCloseTo(2, 6);

    const before = { ...fg.position };
    for (const bad of [0, -1, Number.NaN, Number.POSITIVE_INFINITY]) {
      ref.current!.setCameraRatio(bad);
    }
    expect(fg.position).toEqual(before);
  });

  it("detaches its control listener and its observer on unmount", () => {
    const ref = createRef<F8GraphCanvasHandle>();
    const { view, fg } = render3D({ width: 1440, height: 900 }, ref);
    expect(fg.listenerCount("start")).toBe(1);

    view.unmount();

    expect(fg.listenerCount("start")).toBe(0);
    expect(fg.destroyed).toBe(true);
    expect(liveResizeObservers()).toBe(0);
  });
});
