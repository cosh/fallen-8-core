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
 * DOM-free canvas geometry: the numeric models of what sigma and 3d-force-graph actually draw,
 * kept out of the renderers so they are unit-tested without a WebGL context. Three consumers, one
 * reason to be here:
 * - the hover "eclipse" spotlight (feature canvas-find-connect; the corona itself is pure CSS,
 *   `.eclipse-highlight` in index.css),
 * - the px-to-world size anchor shared by the 3D spheres and the sprites drawn over them,
 * - the camera framing behind the imperative fit/zoom handle (feature canvas-host-controls).
 *
 * Everything here models a THIRD PARTY's arithmetic, so fidelity to the library beats geometric
 * elegance; where the two disagree, the comment says which source it is faithful to and why.
 */

import { isUsableMagnitude, NODE_SIZE_DEFAULT } from "./styleEngine";

/** Node radius (px) floor so a small or distant node still shows a visible corona. */
export const ECLIPSE_MIN_RADIUS = 9;

/** Clamp a raw on-screen node radius (px) up to the visible floor. */
export function eclipseRadius(rawRadius: number): number {
  return Math.max(rawRadius, ECLIPSE_MIN_RADIUS);
}

/**
 * 3d-force-graph's default `nodeRelSize`: the world-space radius it renders per unit of
 * `cbrt(val)`. Named because it is the anchor of every px-to-world conversion on the 3D canvas,
 * and a second copy of it would silently decouple the sphere from the sprite drawn over it.
 */
export const NODE_REL_SIZE = 4;

/**
 * World-space sphere radius for a node value: `cbrt(val) * NODE_REL_SIZE`, collapsing a
 * non-positive value to 0.
 *
 * That floor is where this DIVERGES from the library on purpose. three-forcegraph reads a falsy
 * `nodeVal` as 1 (`valAccessor(node) || 1`), so it would report the DEFAULT radius for val 0 and
 * a negative radius for val below 0 - it is not even self-consistent, clamping with
 * `Math.max(0, val || 1)` on its arrow path but not on its sphere path. Sizing the hover corona
 * off a value the scene cannot honour is worse than sizing it off 0. Unreachable in practice:
 * resolveMagnitudes rejects a non-positive configured magnitude, so a resolved size is always
 * positive; this stays a floor, not a live divergence.
 */
export function worldRadiusForVal(val: number): number {
  return Math.cbrt(Math.max(val, 0)) * NODE_REL_SIZE;
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

/**
 * A 2D node radius in px as a 3d-force-graph node value. `nodeVal` is proportional to sphere
 * VOLUME, so cubing the ratio is what keeps a 3D radius proportional to its 2D counterpart:
 * substituting into `worldRadiusForVal` gives `size * NODE_REL_SIZE / NODE_SIZE_DEFAULT`, making
 * the divisor the constant of proportionality between world units and pixels (0.8 today).
 *
 * The divisor is therefore the DEFAULT node size, never the configured `nodeSize`. Dividing by the
 * configured value would normalise the host's knob away: `nodeSize: 20` would render in 3D exactly
 * like the default while 2D grew fourfold, which is the opposite of keeping the renderers coupled.
 */
export function sizeToVal(size: number): number {
  return Math.pow(size / NODE_SIZE_DEFAULT, 3);
}

/**
 * Sprite edge length per px of 2D radius: an image node's sprite spans the DIAMETER of the sphere
 * it replaces. Derived from the same anchor as `sizeToVal` so the two cannot drift, and exactly
 * the 1.6 this was written as before (`(2 * 4) / 5` is the same double as the literal).
 */
export const SPRITE_SCALE_PER_PX = (2 * NODE_REL_SIZE) / NODE_SIZE_DEFAULT;

/** three.js PerspectiveCamera's default vertical field of view, in degrees. */
export const DEFAULT_FOV = 50;

/**
 * Default fit inset in px: what the 3D mount auto-fit has always used, and the frame
 * `getCameraRatio` measures against until a fit picks another.
 */
export const FIT_PADDING_PX = 60;

/**
 * SIGMA. The camera ratio that frames the whole graph with `paddingPx` of viewport margin.
 *
 * Under `autoRescale` sigma projects the normalised graph through `min(w, h) - 2 * stagePadding` px
 * and then divides by the camera ratio (`matrixFromCamera`), so the quotient of those two spans is
 * the zoom that turns one framing into the other. Asking for the padding already in effect returns
 * exactly 1, which is what a camera reset does, so the default path is a plain reset.
 *
 * Padding is expressed as a ZOOM rather than by writing sigma's `stagePadding` setting, and the two
 * are not interchangeable: `stagePadding` reframes without touching element sizes, whereas a camera
 * ratio also divides every node radius and edge width by `sqrt(ratio)` (`scaleSize`). Zooming is the
 * honest reading of "leave more margin" - a user's wheel-zoom does exactly the same - and it stays
 * transient camera state rather than mutating a persistent setting and forcing a re-index. Label
 * text does NOT shrink with it (`labelSize` is used verbatim), so a fit with a large inset makes
 * labels relatively larger. A host that wants margin WITHOUT a zoom should size its own container.
 *
 * One consequence to know: far enough out, sigma's `minEdgeThickness` floor (Canvas2D sets it)
 * catches ordinary and path-overlay edges alike, so the path's extra WIDTH stops reading. Its colour
 * and its node sizes still do, which is why the overlay stays legible rather than invisible.
 *
 * Padding is clamped because it is a divisor sigma never validates: at 390x100 an unclamped 60 px
 * request yields a NEGATIVE ratio, which renders the graph mirrored. The clamp never bites the stage
 * padding itself, so the omitted-argument case is still exactly 1.
 */
export function fitCameraRatio(
  viewport: { width: number; height: number },
  stagePadding: number,
  paddingPx: number | undefined,
): number {
  const span = Math.min(viewport.width, viewport.height);
  // Omitted, or anything that is not a finite number (a plain-JS host passing null, an unparsed
  // input), means "the frame you already use". This is the ONLY place that decision is made, so no
  // caller repeats it; NaN reaching the camera state would blank the canvas.
  const requested = Number.isFinite(paddingPx) ? (paddingPx as number) : stagePadding;
  // Never below the stage padding itself, so asking for the current frame is still exactly 1.
  const padding = Math.min(Math.max(requested, 0), Math.max(stagePadding, paddingCeiling(span)));
  const framed = span - 2 * stagePadding;
  const target = span - 2 * padding;
  // A viewport too small for its own stage padding collapses sigma's matrix to a point, and no
  // ratio repairs that; leave the camera where a reset would put it.
  return framed > 0 && target > 0 ? framed / target : 1;
}

/**
 * 3D-FORCE-GRAPH. Camera distance FROM THE WORLD ORIGIN at which the whole node cloud just fits a
 * `viewportWidth` x `viewportHeight` frame inset by `paddingPx`, seen through a perspective camera
 * of vertical field of view `fovDeg`. This is the distance `zoomToFit` parks at, so dividing the
 * live camera distance by it gives a ratio that is 1 exactly when the graph fits.
 *
 * Reproduced from three-render-objects' `fitToBbox`, including three things a from-scratch
 * derivation would get "right" and thereby wrong:
 * - the extent is measured from the ORIGIN, not the cloud's centroid, so an off-centre graph pulls
 *   the camera further back (the library re-aims at the origin when it fits),
 * - the divisor is `atan(paddedFov)` rather than the correct `2 * tan(paddedFov / 2)`, which is why
 *   this disagrees with `perspectiveScreenRadius` above: that one must match what WebGL projects,
 *   this one must match where the library moves,
 * - a node contributes its rendered radius, links and arrowheads nothing outside that hull.
 *
 * Returns 0 for "no fit is defined": no placed node, zero extent, a zero-size viewport, or a
 * padding eating at least half the height, which is where `fitToBbox` itself goes to Infinity.
 */
export function graphFitDistance(
  nodes: Iterable<{ val: number; x?: number; y?: number; z?: number }>,
  fovDeg: number,
  viewportWidth: number,
  viewportHeight: number,
  paddingPx: number,
): number {
  if (!(viewportWidth > 0) || !(viewportHeight > 0)) return 0;

  // The max over nodes and axes of |coord| + radius IS fitToBbox's maxBoxSide / 2: its box is
  // measured from the origin, and a node's geometry box is its centre plus or minus its radius.
  let halfExtent = 0;
  for (const node of nodes) {
    const { x = Number.NaN, y = Number.NaN, z = Number.NaN } = node;
    // Unplaced nodes (the force engine has not run yet) have no position to bound.
    if (!Number.isFinite(x) || !Number.isFinite(y) || !Number.isFinite(z)) continue;
    const radius = worldRadiusForVal(node.val);
    halfExtent = Math.max(
      halfExtent,
      Math.abs(x) + radius,
      Math.abs(y) + radius,
      Math.abs(z) + radius,
    );
  }
  if (!(halfExtent > 0)) return 0;

  const paddedFovRad = ((1 - (2 * paddingPx) / viewportHeight) * fovDeg * Math.PI) / 180;
  if (paddedFovRad <= 0) return 0;
  const fitHeightDistance = (2 * halfExtent) / Math.atan(paddedFovRad);
  // fitToBbox takes the larger of the height fit and that fit divided by the aspect ratio.
  const distance = Math.max(
    fitHeightDistance,
    (fitHeightDistance * viewportHeight) / viewportWidth,
  );
  return Number.isFinite(distance) ? distance : 0;
}

/**
 * `position` moved to sit `distance` from the origin along the same direction: the move a fit
 * makes, at a chosen distance. A camera exactly on the origin has no direction to preserve and
 * goes to +z, where the library's own initial camera sits.
 */
export function scaleCameraDistance(
  position: { x: number; y: number; z: number },
  distance: number,
): { x: number; y: number; z: number } {
  const length = Math.hypot(position.x, position.y, position.z);
  if (!(length > 0)) return { x: 0, y: 0, z: distance };
  const scale = distance / length;
  return { x: position.x * scale, y: position.y * scale, z: position.z * scale };
}

/**
 * The most inset a viewport axis can give up and still frame anything: two fifths of it, leaving a
 * fifth in the middle. Both renderers divide by what is left, so both need the same ceiling and it
 * lives here once.
 *
 * Deliberately conservative rather than exact. The arithmetic only actually breaks at half the axis
 * (where the divisor reaches zero), so a two-fifths ceiling also reframes containers a little above
 * that: a 3D canvas between about 120 and 150 px tall now fits slightly closer than it used to.
 * Sitting nearer the real limit would buy those few pixels back and leave almost no margin, which is
 * the wrong trade for a divisor a host can feed anything.
 */
function paddingCeiling(axisPx: number): number {
  return Math.max(axisPx, 0) * 0.4;
}

/**
 * A host-supplied fit inset brought inside what the 3D renderer can honour. Padding is a divisor
 * there too, and the library does NOT guard it: `paddedFov = (1 - 2 * padding / height) * fov`, so
 * a padding of exactly half the height sends the fit distance to Infinity and more than half sends
 * it NEGATIVE, and `fitToBbox` only rejects a distance below zero - it parks the camera at Infinity
 * quite happily. The 0.4 ceiling matches the 2D clamp and leaves a fifth of the field of view.
 */
export function clampFitPadding(paddingPx: number, viewportHeight: number): number {
  const requested = Number.isFinite(paddingPx) ? paddingPx : FIT_PADDING_PX;
  // The fallback goes through the same ceiling as a request: in a 100 px-tall container the default
  // 60 is ITSELF past half the height, so returning it unclamped would reintroduce the bug.
  return Math.min(Math.max(requested, 0), paddingCeiling(viewportHeight));
}

/** A fit tween length: non-finite or negative means the default, and `floorMs` is the renderer's
 *  own lower bound (sigma divides by the duration, so it cannot take 0; three.js treats 0 as
 *  "immediately", which is what a resize-driven fit wants). */
export function fitDurationMs(durationMs: number | undefined, fallback: number, floorMs: number): number {
  const requested = Number.isFinite(durationMs) ? (durationMs as number) : fallback;
  return Math.max(requested, floorMs);
}

/** A camera ratio a host may set: 1 fits, larger is further out. Zero, negative and NaN all blank
 *  the canvas irrecoverably (they divide element sizes and the projection), so they are refused
 *  rather than clamped - silently substituting a number would hide the caller's bug. */
export function isUsableCameraRatio(ratio: number): boolean {
  return isUsableMagnitude(ratio);
}
