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
  clampFitPadding,
  DEFAULT_FOV,
  ECLIPSE_MIN_RADIUS,
  eclipseRadius,
  FIT_PADDING_PX,
  fitCameraRatio,
  fitDurationMs,
  graphFitDistance,
  isUsableCameraRatio,
  NODE_REL_SIZE,
  perspectiveScreenRadius,
  scaleCameraDistance,
  sizeToVal,
  SPRITE_SCALE_PER_PX,
  worldRadiusForVal,
} from "../src/canvas/eclipse";
import { NODE_SIZE_DEFAULT } from "../src/canvas/styleEngine";

/**
 * The DOM-free canvas geometry: the hover "eclipse" corona (feature canvas-find-connect), the
 * px-to-world size anchor, and the camera framing behind the imperative fit/zoom handle (feature
 * canvas-host-controls). All of it models a third party's arithmetic, so the tests below pin it
 * against the LIBRARY's formulas rather than against a tidier derivation - see the module docs.
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


describe("sizeToVal (the px-to-world anchor)", () => {
  it("maps the default node size to val 1, i.e. world radius NODE_REL_SIZE", () => {
    expect(sizeToVal(NODE_SIZE_DEFAULT)).toBe(1);
    expect(worldRadiusForVal(sizeToVal(NODE_SIZE_DEFAULT))).toBeCloseTo(NODE_REL_SIZE, 12);
  });

  it("keeps world radius exactly proportional to the 2D px radius", () => {
    // This is the invariant the whole choice of divisor exists to protect: the 3D sphere must grow
    // in step with the 2D disc, so a host raising nodeSize sees BOTH renderers grow.
    for (const size of [1, 3, 5, 8, 14, 20, 40]) {
      expect(worldRadiusForVal(sizeToVal(size))).toBeCloseTo(
        (size * NODE_REL_SIZE) / NODE_SIZE_DEFAULT,
        10,
      );
    }
  });

  it("anchors on the DEFAULT size, so a quadrupled node size quadruples the 3D radius", () => {
    // Had the divisor been the CONFIGURED size, val would collapse back to 1 here and the 3D frame
    // would be identical to the default while 2D grew fourfold.
    expect(sizeToVal(4 * NODE_SIZE_DEFAULT)).toBe(64);
    expect(worldRadiusForVal(sizeToVal(4 * NODE_SIZE_DEFAULT))).toBeCloseTo(4 * NODE_REL_SIZE, 10);
  });

  it("scales an image sprite to the diameter of the sphere it replaces", () => {
    // Bit-identical to the 1.6 this was written as before the anchor was named, which is what makes
    // image nodes pixel-identical rather than merely close: (2 * 4) / 5 and 1.6 are the same double.
    expect(SPRITE_SCALE_PER_PX).toBe(1.6);
    // The diameter relation itself is only APPROXIMATE, and deliberately not the shipped formula:
    // routing through cbrt(pow(x, 3)) disagrees with a plain multiply in the last bit for about a
    // third of all sizes (3 * 1.6 is 4.800000000000001, 2 * cbrt(0.216) * 4 is 4.8). The renderer
    // multiplies, so it keeps the old pixels; the round-trip is the check on the geometry.
    for (const size of [3, 5, 8.5, 14]) {
      expect(size * SPRITE_SCALE_PER_PX).toBeCloseTo(2 * worldRadiusForVal(sizeToVal(size)), 10);
    }
  });
});

describe("fitCameraRatio (2D fit)", () => {
  const STAGE = 30; // sigma's default stagePadding

  it("is exactly 1 when the caller asks for the padding already in effect", () => {
    // The load-bearing case: fitToView() with no padding must be a plain camera reset, so the
    // default path cannot move a single pixel relative to the previous behaviour.
    for (const viewport of [
      { width: 390, height: 844 },
      { width: 1440, height: 900 },
      { width: 3840, height: 2160 },
    ]) {
      expect(fitCameraRatio(viewport, STAGE, STAGE)).toBe(1);
    }
  });

  it("zooms out for more padding than the stage already leaves", () => {
    // span - 2*stage over span - 2*padding, on the SHORTER axis.
    expect(fitCameraRatio({ width: 390, height: 844 }, STAGE, 60)).toBeCloseTo(330 / 270, 12);
    expect(fitCameraRatio({ width: 1440, height: 900 }, STAGE, 60)).toBeCloseTo(840 / 780, 12);
    expect(fitCameraRatio({ width: 3840, height: 2160 }, STAGE, 60)).toBeCloseTo(2100 / 2040, 12);
  });

  it("zooms in for less padding, and treats a negative request as zero", () => {
    expect(fitCameraRatio({ width: 1440, height: 900 }, STAGE, 0)).toBeCloseTo(840 / 900, 12);
    expect(fitCameraRatio({ width: 1440, height: 900 }, STAGE, -100)).toBe(
      fitCameraRatio({ width: 1440, height: 900 }, STAGE, 0),
    );
  });

  it("clamps an absurd padding instead of inverting the stage", () => {
    // Unclamped, 60 px of padding in a 100 px-tall box gives a NEGATIVE ratio, which renders the
    // graph mirrored. The clamp caps padding at 0.4 * span, so this stays positive and finite.
    const ratio = fitCameraRatio({ width: 390, height: 100 }, STAGE, 60);
    expect(ratio).toBe(2); // (100 - 60) / (100 - 2*40)
    expect(ratio).toBeGreaterThan(0);
    expect(fitCameraRatio({ width: 390, height: 100 }, STAGE, 10_000)).toBe(2);
  });

  it("falls back to 1 for a viewport too small for its own stage padding", () => {
    expect(fitCameraRatio({ width: 50, height: 50 }, STAGE, 5)).toBe(1);
    expect(fitCameraRatio({ width: 0, height: 0 }, STAGE, 5)).toBe(1);
  });

  it("reads an omitted or non-finite padding as 'the frame you already use', never NaN", () => {
    // A plain-JS host can pass null or an unparsed input; NaN in the camera state blanks the canvas.
    expect(fitCameraRatio({ width: 1440, height: 900 }, STAGE, undefined)).toBe(1);
    expect(fitCameraRatio({ width: 1440, height: 900 }, STAGE, Number.NaN)).toBe(1);
    expect(fitCameraRatio({ width: 1440, height: 900 }, STAGE, Number.POSITIVE_INFINITY)).toBe(1);
  });
});

describe("graphFitDistance (3D fit)", () => {
  /** three-render-objects' fitToBbox, transcribed, as the oracle this must agree with. */
  function libraryFitDistance(
    nodes: { val: number; x: number; y: number; z: number }[],
    fovDeg: number,
    width: number,
    height: number,
    padding: number,
  ): number {
    const bbox = { x: [Infinity, -Infinity], y: [Infinity, -Infinity], z: [Infinity, -Infinity] };
    for (const node of nodes) {
      const r = worldRadiusForVal(node.val);
      for (const axis of ["x", "y", "z"] as const) {
        bbox[axis][0] = Math.min(bbox[axis][0], node[axis] - r);
        bbox[axis][1] = Math.max(bbox[axis][1], node[axis] + r);
      }
    }
    // The library measures the box from the ORIGIN, not from the cloud's centre.
    const maxBoxSide =
      Math.max(...Object.values(bbox).map((coords) => Math.max(...coords.map(Math.abs)))) * 2;
    const paddedFov = (1 - (padding * 2) / height) * fovDeg;
    const fitHeightDistance = maxBoxSide / Math.atan((paddedFov * Math.PI) / 180);
    return Math.max(fitHeightDistance, fitHeightDistance / (width / height));
  }

  const CLOUD = [
    { val: 1, x: 10, y: -20, z: 5 },
    { val: 8, x: -40, y: 15, z: -3 },
    { val: 27, x: 5, y: 60, z: 12 },
  ];

  it("agrees with the library's own fitToBbox arithmetic", () => {
    for (const [width, height] of [
      [390, 844],
      [1440, 900],
      [3840, 2160],
      [900, 1440],
    ]) {
      for (const padding of [0, 60, 200]) {
        expect(graphFitDistance(CLOUD, DEFAULT_FOV, width, height, padding)).toBeCloseTo(
          libraryFitDistance(CLOUD, DEFAULT_FOV, width, height, padding),
          6,
        );
      }
    }
  });

  it("pulls the camera further back for an off-centre cloud (the box is measured from the origin)", () => {
    const centred = [{ val: 1, x: 0, y: 0, z: 0 }, { val: 1, x: 10, y: 0, z: 0 }];
    const offCentre = centred.map((n) => ({ ...n, x: n.x + 500 }));
    expect(graphFitDistance(offCentre, DEFAULT_FOV, 1440, 900, 60)).toBeGreaterThan(
      graphFitDistance(centred, DEFAULT_FOV, 1440, 900, 60),
    );
  });

  it("grows with the node radii, not just the positions", () => {
    const small = [{ val: 1, x: 0, y: 0, z: 0 }];
    const large = [{ val: 1000, x: 0, y: 0, z: 0 }];
    expect(graphFitDistance(large, DEFAULT_FOV, 1440, 900, 60)).toBeGreaterThan(
      graphFitDistance(small, DEFAULT_FOV, 1440, 900, 60),
    );
  });

  it("returns 0 where no fit is defined, never Infinity or a negative distance", () => {
    expect(graphFitDistance([], DEFAULT_FOV, 1440, 900, 60)).toBe(0);
    // Unplaced nodes: the force engine has not run yet.
    expect(graphFitDistance([{ val: 1 }], DEFAULT_FOV, 1440, 900, 60)).toBe(0);
    expect(graphFitDistance([{ val: 1, x: 0, y: 0, z: 0 }], DEFAULT_FOV, 0, 900, 60)).toBe(0);
    expect(graphFitDistance(CLOUD, DEFAULT_FOV, 1440, 900, 450)).toBe(0); // padding == height/2
    expect(graphFitDistance(CLOUD, DEFAULT_FOV, 1440, 900, 600)).toBe(0); // padding > height/2
  });

  it("skips an unplaced node but still fits the placed ones", () => {
    const mixed = [{ val: 1, x: 30, y: 0, z: 0 }, { val: 1 }];
    expect(graphFitDistance(mixed, DEFAULT_FOV, 1440, 900, 60)).toBeCloseTo(
      graphFitDistance([{ val: 1, x: 30, y: 0, z: 0 }], DEFAULT_FOV, 1440, 900, 60),
      12,
    );
  });
});

describe("scaleCameraDistance", () => {
  it("keeps the direction and takes the requested distance from the origin", () => {
    const moved = scaleCameraDistance({ x: 3, y: 4, z: 0 }, 50);
    expect(Math.hypot(moved.x, moved.y, moved.z)).toBeCloseTo(50, 10);
    // Same bearing: 3-4-0 scaled tenfold.
    expect(moved.x).toBeCloseTo(30, 10);
    expect(moved.y).toBeCloseTo(40, 10);
    expect(moved.z).toBe(0);
  });

  it("sends a camera sitting on the origin to +z, where the library's own camera starts", () => {
    expect(scaleCameraDistance({ x: 0, y: 0, z: 0 }, 120)).toEqual({ x: 0, y: 0, z: 120 });
  });
});

describe("host-supplied number guards", () => {
  it("clamps a 3D fit padding below the half-height that sends the fit to Infinity", () => {
    expect(clampFitPadding(60, 900)).toBe(60);
    expect(clampFitPadding(450, 900)).toBe(360); // 0.4 * 900, comfortably under height/2
    expect(clampFitPadding(-10, 900)).toBe(0);
    expect(clampFitPadding(Number.NaN, 900)).toBe(FIT_PADDING_PX);
    // The FALLBACK is clamped too: in a 100 px-tall box the default 60 is itself past half the
    // height, so handing it back unclamped would put the fit right back in the broken region.
    expect(clampFitPadding(Number.NaN, 100)).toBe(40);
    expect(clampFitPadding(undefined as unknown as number, 100)).toBe(40);
    // A collapsed container leaves no room for any inset at all.
    expect(clampFitPadding(60, 0)).toBe(0);
  });

  it("keeps every clamped 3D padding out of the Infinity and negative-distance regions", () => {
    const cloud = [{ val: 8, x: 20, y: -30, z: 10 }];
    for (const height of [100, 390, 900, 2160]) {
      for (const requested of [0, 60, height / 2, height, 10_000]) {
        const distance = graphFitDistance(
          cloud,
          DEFAULT_FOV,
          1440,
          height,
          clampFitPadding(requested, height),
        );
        expect(Number.isFinite(distance)).toBe(true);
        expect(distance).toBeGreaterThan(0);
      }
    }
  });

  it("substitutes a usable duration and honours the renderer's floor", () => {
    expect(fitDurationMs(250, 600, 1)).toBe(250);
    expect(fitDurationMs(undefined, 600, 1)).toBe(600);
    expect(fitDurationMs(Number.NaN, 600, 1)).toBe(600);
    // Sigma divides by the duration, so 0 must never reach it; three.js reads 0 as "move now".
    expect(fitDurationMs(0, 600, 1)).toBe(1);
    expect(fitDurationMs(0, 600, 0)).toBe(0);
    expect(fitDurationMs(-5, 600, 0)).toBe(0);
  });

  it("refuses a camera ratio that would blank the canvas", () => {
    expect(isUsableCameraRatio(1)).toBe(true);
    expect(isUsableCameraRatio(0.25)).toBe(true);
    expect(isUsableCameraRatio(0)).toBe(false);
    expect(isUsableCameraRatio(-1)).toBe(false);
    expect(isUsableCameraRatio(Number.NaN)).toBe(false);
    expect(isUsableCameraRatio(Number.POSITIVE_INFINITY)).toBe(false);
  });
});
