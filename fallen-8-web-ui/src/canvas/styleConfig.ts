// MIT License
//
// styleConfig.ts
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
 * Canvas style configuration (feature studio-canvas-viz): what the style panel edits,
 * what the store persists per instance, and what the style engine resolves against.
 * Defaults reproduce the pre-feature rendering exactly (spec FR-8).
 */

export type CanvasRenderer = "2d" | "3d";

export type Layout2d = "force" | "circular" | "circlepack" | "grid" | "random";
export type Layout3d = "force" | "dag-td" | "dag-radial";

/** How a color is chosen: stable label hash (default) or a user-named property. */
export type ColorMode = "label" | "property";

/** Node size source; degree modes count edges visible on the canvas (spec: Decisions). */
export type NodeSizeMode = "fixed" | "property" | "in-degree" | "out-degree" | "degree";

export type EdgeWidthMode = "fixed" | "property";

export interface StyleConfig {
  renderer: CanvasRenderer;
  layout2d: Layout2d;
  layout3d: Layout3d;

  nodeColorMode: ColorMode;
  nodeColorProperty: string;
  nodeSizeMode: NodeSizeMode;
  nodeSizeProperty: string;
  /** Property whose value renders as the node: http(s)/data URL or emoji/text (FR-5). */
  nodeImageProperty: string;

  edgeColorMode: ColorMode;
  edgeColorProperty: string;
  edgeWidthMode: EdgeWidthMode;
  edgeWidthProperty: string;

  showNodeLabels: boolean;
  showEdgeLabels: boolean;
  edgeArrows: boolean;

  // ---- magnitudes (feature canvas-host-controls) ----
  //
  // Every field below is OPTIONAL and omitting all of them reproduces the rendering exactly,
  // which is what keeps F8GraphCanvasProps a frozen contract for hosts that pin a tag. They
  // exist because sigma sizes are absolute px: the same graph that reads well in a 1440 px
  // box is a hairline star in a 3840 px one, and the host is the only party that knows how
  // big its box is. Sizes stay deterministic on purpose - nothing here scales itself with the
  // viewport or the device pixel ratio; a host computes what it wants from the exported
  // defaults (NODE_SIZE_DEFAULT and friends in styleEngine.ts) and passes numbers in.
  //
  // Every default is NAMED below rather than spelled out, because the constant in styleEngine.ts is
  // that value's one home and a number copied into a doc comment is the copy that goes stale.
  // resolveMagnitudes() there is what applies them, treating any value that is not a finite positive
  // number as omitted.

  /**
   * Node radius in px for `nodeSizeMode: "fixed"`. Also the fallback the scaled modes use for
   * a node they cannot measure (no such property, or a non-numeric value).
   * Default: `NODE_SIZE_DEFAULT`.
   */
  nodeSize?: number;
  /** `[min, max]` node radius in px for the scaled modes. Default: `NODE_SIZE_RANGE`. */
  nodeSizeRange?: readonly [number, number];
  /** Edge width in px for `edgeWidthMode: "fixed"`, and the unmeasurable-edge fallback. Default: `EDGE_WIDTH_DEFAULT`. */
  edgeWidth?: number;
  /** `[min, max]` edge width in px for the scaled mode. Default: `EDGE_WIDTH_RANGE`. */
  edgeWidthRange?: readonly [number, number];
  /** Node label px. Default: `LABEL_SIZE_DEFAULT`. */
  labelSize?: number;
  /** Edge label px. Default: `EDGE_LABEL_SIZE_DEFAULT`. */
  edgeLabelSize?: number;
}

export const DEFAULT_STYLE_CONFIG: StyleConfig = {
  renderer: "2d",
  layout2d: "force",
  layout3d: "force",

  nodeColorMode: "label",
  nodeColorProperty: "",
  nodeSizeMode: "fixed",
  nodeSizeProperty: "",
  nodeImageProperty: "",

  edgeColorMode: "label",
  edgeColorProperty: "",
  edgeWidthMode: "fixed",
  edgeWidthProperty: "",

  showNodeLabels: true,
  showEdgeLabels: true,
  edgeArrows: false,

  // The magnitudes are deliberately ABSENT rather than spelled out here. This object seeds and
  // is merged into the persisted per-instance style config (instanceStore.ts), with the
  // persisted copy winning, so a magnitude written here would be frozen into every existing
  // workspace's local storage and would then outrank the documented default forever.
};
