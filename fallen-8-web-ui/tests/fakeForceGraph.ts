// MIT License
//
// fakeForceGraph.ts
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
 * The ONE 3d-force-graph test double. Canvas3D drives a fluent WebGL library jsdom cannot run, so
 * every test that renders it replaces the module. Most of that surface only needs to chain, which is
 * what the Proxy below gives for free; the handful of calls the camera handle actually depends on are
 * modelled for real, and recorded so a test can assert them.
 *
 * `zoomToFit` is modelled the important way: it reproduces three-render-objects' own `fitToBbox`
 * arithmetic and MOVES the camera to the distance the real library would pick. That is what lets a
 * test assert "getCameraRatio() is 1 right after a fit" without using the code under test as its own
 * oracle - the fake speaks the library's formula, the component speaks its own.
 *
 * Usage:
 *   vi.mock("3d-force-graph", () => import("./fakeForceGraph").then((m) => ({ default: m.FakeForceGraph })));
 */

type Vec = { x: number; y: number; z: number };
type FitNode = { val: number; x?: number; y?: number; z?: number };

/** Every fake constructed in the current test file, newest last. */
export const forceGraphInstances: FakeForceGraphState[] = [];

/** three-render-objects' fitToBbox, transcribed: the box is measured from the ORIGIN, and the
 *  divisor is atan(paddedFov) rather than the geometrically correct 2*tan(paddedFov/2). */
function libraryFitDistance(nodes: FitNode[], fov: number, width: number, height: number, padding: number): number {
  let half = 0;
  for (const n of nodes) {
    if (!Number.isFinite(n.x) || !Number.isFinite(n.y) || !Number.isFinite(n.z)) continue;
    const r = Math.cbrt(Math.max(n.val, 0)) * 4; // nodeRelSize
    half = Math.max(half, Math.abs(n.x!) + r, Math.abs(n.y!) + r, Math.abs(n.z!) + r);
  }
  if (half <= 0) return 0;
  const paddedFov = (1 - (2 * padding) / height) * fov;
  const fitHeight = (2 * half) / Math.atan((paddedFov * Math.PI) / 180);
  return Math.max(fitHeight, fitHeight / (width / height));
}

export class FakeForceGraphState {
  widthPx = 1440;
  heightPx = 900;
  fov = 50;
  /** Camera position in world space; the library's own initial camera sits on +z. */
  position: Vec = { x: 0, y: 0, z: 1000 };
  lookAt: Vec | null = null;
  readonly zoomToFitCalls: { durationMs: number; padding: number }[] = [];
  readonly graphDataCalls: { nodes: FitNode[] }[] = [];
  destroyed = false;
  private readonly listeners = new Map<string, Set<() => void>>();

  width(px?: number) {
    if (px === undefined) return this.widthPx;
    this.widthPx = px;
    return undefined;
  }

  height(px?: number) {
    if (px === undefined) return this.heightPx;
    this.heightPx = px;
    return undefined;
  }

  camera() {
    return { fov: this.fov, position: this.position };
  }

  cameraPosition(position?: Partial<Vec>, lookAt?: Vec) {
    if (position === undefined) return this.position;
    this.position = { ...this.position, ...position };
    this.lookAt = lookAt ?? { x: 0, y: 0, z: 0 };
    return undefined;
  }

  graphData(data?: { nodes: FitNode[] }) {
    if (data === undefined) return this.graphDataCalls.at(-1) ?? { nodes: [] };
    this.graphDataCalls.push(data);
    return undefined;
  }

  /** Records the call AND parks the camera where the real library would, per its own formula. */
  zoomToFit(durationMs = 0, padding = 10) {
    this.zoomToFitCalls.push({ durationMs, padding });
    const nodes = this.graphDataCalls.at(-1)?.nodes ?? [];
    const distance = libraryFitDistance(nodes, this.fov, this.widthPx, this.heightPx, padding);
    if (distance > 0) {
      const length = Math.hypot(this.position.x, this.position.y, this.position.z) || 1;
      const scale = distance / length;
      this.position = {
        x: this.position.x * scale,
        y: this.position.y * scale,
        z: this.position.z * scale,
      };
      this.lookAt = { x: 0, y: 0, z: 0 };
    }
    return undefined;
  }

  controls() {
    return {
      addEventListener: (type: string, listener: () => void) => {
        const set = this.listeners.get(type) ?? new Set();
        set.add(listener);
        this.listeners.set(type, set);
      },
      removeEventListener: (type: string, listener: () => void) => {
        this.listeners.get(type)?.delete(listener);
      },
    };
  }

  /** Stand in for the visitor grabbing the orbit controls. */
  emitControls(type: string): void {
    for (const listener of this.listeners.get(type) ?? []) listener();
  }

  /** How many listeners are still attached, so a test can pin teardown. */
  listenerCount(type: string): number {
    return this.listeners.get(type)?.size ?? 0;
  }

  _destructor() {
    this.destroyed = true;
    return undefined;
  }
}

/**
 * The constructor Canvas3D calls. Returns a Proxy so any fluent setter the component uses but this
 * fake does not model still chains, which is what keeps the double from breaking every time Canvas3D
 * calls one more option.
 */
export function FakeForceGraph(this: unknown) {
  const state = new FakeForceGraphState();
  forceGraphInstances.push(state);
  const proxy: unknown = new Proxy(state, {
    get(target, prop, receiver) {
      if (prop in target) {
        const value = Reflect.get(target, prop, receiver);
        if (typeof value !== "function") return value;
        return (...args: unknown[]) => {
          const result = (value as (...a: unknown[]) => unknown).apply(state, args);
          // undefined means "a fluent setter": hand the chain back, like the real library.
          return result === undefined ? proxy : result;
        };
      }
      return () => proxy;
    },
  });
  return proxy;
}

/** Cleared per test by the global afterEach. */
export function resetForceGraphInstances(): void {
  forceGraphInstances.length = 0;
}
