// MIT License
//
// fakeSigma.ts
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

/**
 * The ONE Sigma test double. Sigma needs a WebGL context jsdom has no answer for, so every test
 * that renders Canvas2D replaces the module; this used to be a hand-rolled class per test file,
 * which meant every method Canvas2D newly called broke three files at once. Instead: one fake
 * that records what the component did to it, and one place to extend.
 *
 * Usage, in a test file (the factory must dynamically import - vi.mock is hoisted above imports):
 *
 *   vi.mock("sigma", () => import("./fakeSigma").then((m) => ({ default: m.FakeSigma })));
 *   vi.mock("sigma/rendering", () => import("./fakeSigma").then((m) => m.sigmaRenderingModule));
 */

type Handler = (payload: { node?: string; edge?: string }) => void;

/** Every fake constructed in the current test file, newest last. */
export const sigmaInstances: FakeSigma[] = [];

/** The camera surface the fit/zoom handle touches, recording what it was asked to do. */
export class FakeCamera {
  ratio = 1;
  x = 0.5;
  y = 0.5;
  angle = 0;
  /** Every animate() call, in order: the target state and the options it was given. */
  readonly animations: { state: Record<string, number>; options: Record<string, unknown> }[] = [];
  /** Every setState() call, in order. */
  readonly stateWrites: Record<string, number>[] = [];

  animate(state: Record<string, number>, options: Record<string, unknown> = {}) {
    this.animations.push({ state, options });
    Object.assign(this, state);
    return Promise.resolve();
  }

  setState(state: Record<string, number>) {
    this.stateWrites.push(state);
    Object.assign(this, state);
    return this;
  }
}

export class FakeSigma {
  /** Handlers Canvas2D registered, by event name, so a test can fire a click. */
  readonly handlers: Record<string, Handler> = {};
  /** The merged settings from every setSettings call, so a test can assert what landed. */
  readonly settings: Record<string, unknown> = {};
  /** The settings the CONSTRUCTOR was given, kept apart from later setSettings writes. */
  readonly constructorSettings: Record<string, unknown>;
  readonly camera = new FakeCamera();
  /** What getDimensions() reports; a test changes this to model a container resize. */
  dimensions = { width: 1440, height: 900 };
  stagePadding = 30;
  killed = false;
  refreshCount = 0;
  scheduledRefreshCount = 0;
  scheduledRenderCount = 0;
  resizeCount = 0;

  constructor(_graph?: unknown, _container?: unknown, settings?: Record<string, unknown>) {
    this.constructorSettings = settings ?? {};
    sigmaInstances.push(this);
  }

  on(event: string, handler: Handler) {
    this.handlers[event] = handler;
  }

  getCamera() {
    return this.camera;
  }

  getDimensions() {
    return this.dimensions;
  }

  getStagePadding() {
    return this.stagePadding;
  }

  resize() {
    this.resizeCount++;
    return this;
  }

  scheduleRender() {
    this.scheduledRenderCount++;
  }

  setSettings(settings: Record<string, unknown>) {
    Object.assign(this.settings, settings);
    return this;
  }

  refresh() {
    this.refreshCount++;
  }

  scheduleRefresh() {
    this.scheduledRefreshCount++;
  }

  kill() {
    this.killed = true;
  }
}

/** Cleared per test by the global afterEach: instances from an earlier test are not this test's. */
export function resetSigmaInstances(): void {
  sigmaInstances.length = 0;
}

/** The shader/program classes Canvas2D passes to the constructor; identity is all that matters. */
export const sigmaRenderingModule = {
  EdgeArrowProgram: class {},
  EdgeRectangleProgram: class {},
  NodeCircleProgram: class {},
};

export const sigmaNodeImageModule = { createNodeImageProgram: () => class {} };

/**
 * The parallel-edge helper is a real algorithm Canvas2D depends on, so the fake does what the
 * component needs to stay on its non-parallel branch: mark every edge as unindexed.
 */
export const sigmaEdgeCurveModule = {
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
};

/** The FA2 worker and its settings helper: the layout effect must start and stop something. */
export const fa2WorkerModule = {
  default: class {
    start() {}
    stop() {}
    kill() {}
  },
};

export const fa2Module = { default: { inferSettings: () => ({}) } };
