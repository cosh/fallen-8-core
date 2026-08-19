// MIT License
//
// canvas.ts
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
 * The canvas-only export surface, published as the package subpath `./canvas`. This is the
 * ONE home for what a canvas host may import; the whole-Studio entry (index.ts) re-exports
 * this list rather than repeating it, so the two entries can never drift.
 *
 * Why a second entry exists: importing the canvas from the main entry drags the app shell's
 * module graph (the Monaco editor above all) into the host's bundle. A host that wants a
 * graph and nothing else imports `@fallen-8/studio/canvas` and pays for the graph only -
 * scripts/check-lib-artifact.mjs fails the build if the editor leaks back in.
 */

export { F8GraphCanvas, type F8GraphCanvasProps } from "./F8GraphCanvas";
export type { ElementRef, F8GraphCanvasHandle } from "../canvas/GraphCanvas";
export { DEFAULT_STYLE_CONFIG } from "../canvas/styleConfig";
export type { StyleConfig } from "../canvas/styleConfig";
export type { CanvasEdge, CanvasNode } from "../state/instanceStore";
export type { PathREST } from "../api/types";
export type { ThemeTokens } from "../app/studioConfig";

/**
 * The documented default magnitudes. A host that computes its own sizes (say, scaling with
 * its container) needs to know what it is scaling FROM, so the defaults are part of the
 * contract rather than private constants.
 */
export {
  EDGE_LABEL_SIZE_DEFAULT,
  EDGE_WIDTH_DEFAULT,
  EDGE_WIDTH_RANGE,
  LABEL_SIZE_DEFAULT,
  NODE_SIZE_DEFAULT,
  NODE_SIZE_RANGE,
  PATH_EDGE_MIN_WIDTH,
  PATH_NODE_MIN_SIZE,
} from "../canvas/styleEngine";
