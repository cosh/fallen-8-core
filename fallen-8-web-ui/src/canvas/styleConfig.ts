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
};
