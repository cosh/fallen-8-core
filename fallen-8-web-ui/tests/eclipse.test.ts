// MIT License
//
// eclipse.test.ts
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

import { describe, expect, it } from "vitest";
import {
  ECLIPSE_MIN_RADIUS,
  eclipseRadius,
  perspectiveScreenRadius,
  worldRadiusForVal,
} from "../src/canvas/eclipse";

/**
 * The DOM-free geometry behind the Canvas hover "eclipse" (feature canvas-find-connect): the 3D
 * renderer sizes the corona from these, so they are pinned here without a WebGL context.
 */

describe("worldRadiusForVal", () => {
  it("is cbrt(val) * 4 (3d-force-graph's default nodeRelSize)", () => {
    expect(worldRadiusForVal(1)).toBeCloseTo(4);
    expect(worldRadiusForVal(8)).toBeCloseTo(8);
    expect(worldRadiusForVal(27)).toBeCloseTo(12);
  });

  it("collapses a zero or negative value to 0 rather than NaN", () => {
    expect(worldRadiusForVal(0)).toBe(0);
    expect(worldRadiusForVal(-5)).toBe(0);
  });
});

describe("perspectiveScreenRadius", () => {
  it("projects a known geometry to the expected pixel radius", () => {
    // fov 90deg -> tan(45) = 1 -> projectionScale = height / 2 = 500; (worldR/dist) * 500.
    expect(perspectiveScreenRadius(10, 1, 90, 1000)).toBeCloseTo(50);
    expect(perspectiveScreenRadius(5, 1, 90, 1000)).toBeCloseTo(100);
  });

  it("grows as the camera nears and as the sphere grows", () => {
    const near = perspectiveScreenRadius(5, 1, 60, 800);
    const far = perspectiveScreenRadius(20, 1, 60, 800);
    expect(near).toBeGreaterThan(far);
    const big = perspectiveScreenRadius(10, 2, 60, 800);
    const small = perspectiveScreenRadius(10, 1, 60, 800);
    expect(big).toBeGreaterThan(small);
  });

  it("returns 0 for a non-positive distance instead of dividing by zero", () => {
    expect(perspectiveScreenRadius(0, 1, 60, 800)).toBe(0);
    expect(perspectiveScreenRadius(-3, 1, 60, 800)).toBe(0);
  });
});

describe("eclipseRadius", () => {
  it("floors a small, zero, or negative radius to ECLIPSE_MIN_RADIUS", () => {
    // The floor keeps a tiny or far node's corona visible (and a 0px radius from a
    // distance-collapsed perspectiveScreenRadius never renders invisibly).
    expect(eclipseRadius(0)).toBe(ECLIPSE_MIN_RADIUS);
    expect(eclipseRadius(3)).toBe(ECLIPSE_MIN_RADIUS);
    expect(eclipseRadius(-5)).toBe(ECLIPSE_MIN_RADIUS);
  });

  it("passes a radius above the floor through unchanged", () => {
    expect(eclipseRadius(ECLIPSE_MIN_RADIUS + 20)).toBe(ECLIPSE_MIN_RADIUS + 20);
  });
});
