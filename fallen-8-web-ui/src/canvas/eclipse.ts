// MIT License
//
// eclipse.ts
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
 * Geometry helpers for the canvas hover "eclipse" spotlight (feature canvas-find-connect). The
 * corona itself is pure CSS (`.eclipse-highlight` in index.css); these are the DOM-free bits the
 * 3D renderer needs to size it, kept here so they are unit-tested without a WebGL context.
 */

/** Node radius (px) floor so a small or distant node still shows a visible corona. */
export const ECLIPSE_MIN_RADIUS = 9;

/** Clamp a raw on-screen node radius (px) up to the visible floor. */
export function eclipseRadius(rawRadius: number): number {
  return Math.max(rawRadius, ECLIPSE_MIN_RADIUS);
}

/**
 * The world-space sphere radius 3d-force-graph renders for a node value: `cbrt(val) * nodeRelSize`
 * with the library's default `nodeRelSize` of 4. A non-positive value collapses to 0.
 */
export function worldRadiusForVal(val: number): number {
  return Math.cbrt(Math.max(val, 0)) * 4;
}

/**
 * Screen-space radius (px) of a sphere of world radius `worldRadius`, seen at camera `distance`
 * through a perspective camera of vertical field of view `fovDeg`, in a viewport `viewportHeight`
 * px tall. Closer or larger spheres project bigger; a non-positive distance (camera on the node)
 * yields 0 rather than a divide-by-zero.
 */
export function perspectiveScreenRadius(
  distance: number,
  worldRadius: number,
  fovDeg: number,
  viewportHeight: number,
): number {
  if (distance <= 0) return 0;
  const halfFovRad = (fovDeg * Math.PI) / 360; // (fovDeg / 2) in radians
  const projectionScale = viewportHeight / (2 * Math.tan(halfFovRad));
  return (worldRadius / distance) * projectionScale;
}
