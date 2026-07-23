import type { CanvasEdge, CanvasNode } from "../state/instanceStore";
import type { ColorMode, StyleConfig } from "./styleConfig";
import { colorForLabel, colorForValue, gradientColor, UNLABELED_COLOR } from "./styling";

/**
 * Pure style resolution (studio-canvas-viz, spec "Decisions"): maps the canvas model +
 * style config + path overlay to per-element visuals. This is the ONE home for the
 * styling rules — the 2D and 3D renderers only consume what is resolved here.
 */

export interface PathOverlaySets {
  nodeIds: Set<number>;
  edgeIds: Set<number>;
  active: boolean;
  /** true: non-members grey out (path overlay); false: members pop, the rest keeps its colors (adjacency-preview emphasis). */
  dim: boolean;
}

export const EMPTY_OVERLAY: PathOverlaySets = {
  nodeIds: new Set(),
  edgeIds: new Set(),
  active: false,
  dim: false,
};

/** What a node image property value turned out to be (FR-5). */
export interface ImageSpec {
  kind: "url" | "emoji";
  value: string;
}

export interface ResolvedNodeStyle {
  color: string;
  size: number;
  image: ImageSpec | null;
  zIndex: number;
  dimmed: boolean;
}

export interface ResolvedEdgeStyle {
  color: string;
  width: number;
  zIndex: number;
  dimmed: boolean;
}

export interface ResolvedStyles {
  nodes: Record<number, ResolvedNodeStyle>;
  edges: Record<number, ResolvedEdgeStyle>;
}

// Size/width ranges (FR-3/FR-4). Defaults match the pre-feature constants.
export const NODE_SIZE_DEFAULT = 5;
export const NODE_SIZE_RANGE: readonly [number, number] = [3, 14];
export const EDGE_WIDTH_DEFAULT = 1;
export const EDGE_WIDTH_RANGE: readonly [number, number] = [0.5, 5];

// Path overlay visuals (FR-9), unchanged from the pre-feature canvas.
export const PATH_NODE_MIN_SIZE = 8;
export const PATH_EDGE_MIN_WIDTH = 3;
export const PATH_EDGE_COLOR = "#4cc38a";
export const DIM_NODE_COLOR = "#2a3240";
export const DIM_EDGE_COLOR = "#1a2029";
export const UNLABELED_EDGE_COLOR = "#2c3543";

type StyledElement = { label: string | null; props?: Record<string, string | number | boolean> };

function propValue(el: StyledElement, property: string): unknown {
  return (el.props ?? {})[property];
}

/** Numeric magnitude of a property value; booleans are categories, never magnitudes. */
export function numericValue(value: unknown): number | null {
  if (typeof value === "number" && Number.isFinite(value)) return value;
  if (typeof value === "string" && value.trim() !== "") {
    const n = Number(value);
    return Number.isFinite(n) ? n : null;
  }
  return null;
}

/** FR-5: http(s)/data URLs load as images; any other scalar rasterizes as emoji/text. */
export function classifyImageValue(value: unknown): ImageSpec | null {
  if (value === null || value === undefined || typeof value === "boolean") return null;
  const text = String(value).trim();
  if (!text) return null;
  if (/^(https?:\/\/|data:)/i.test(text)) return { kind: "url", value: text };
  // Code-point cap keeps accidental long strings from becoming wall-of-text sprites
  // while leaving multi-code-point emoji (ZWJ sequences, flags) intact.
  return { kind: "emoji", value: [...text].slice(0, 8).join("") };
}

/** How a color mode + property resolve over a concrete element set (FR-1/FR-2, FR-10). */
export type ColorScale =
  | { kind: "label" }
  | { kind: "categorical"; property: string }
  | { kind: "gradient"; property: string; min: number; max: number };

export function buildColorScale(
  elements: StyledElement[],
  mode: ColorMode,
  property: string,
): ColorScale {
  if (mode !== "property" || !property) return { kind: "label" };
  const present = elements
    .map((el) => propValue(el, property))
    .filter((v) => v !== undefined && v !== "");
  if (present.length > 0 && present.every((v) => numericValue(v) !== null)) {
    const numbers = present.map((v) => numericValue(v)!);
    return {
      kind: "gradient",
      property,
      min: Math.min(...numbers),
      max: Math.max(...numbers),
    };
  }
  return { kind: "categorical", property };
}

function colorFromScale(scale: ColorScale, el: StyledElement, labelFallback: string): string {
  if (scale.kind === "label") return labelFallback;
  const value = propValue(el, scale.property);
  if (value === undefined || value === "") return UNLABELED_COLOR;
  if (scale.kind === "gradient") {
    const n = numericValue(value);
    if (n === null) return UNLABELED_COLOR;
    const span = scale.max - scale.min;
    return gradientColor(span === 0 ? 0.5 : (n - scale.min) / span);
  }
  return colorForValue(value);
}

function scaleInto(
  values: (number | null)[],
  range: readonly [number, number],
  fallback: number,
): (v: number | null) => number {
  const present = values.filter((v): v is number => v !== null);
  if (present.length === 0) return () => fallback;
  const min = Math.min(...present);
  const max = Math.max(...present);
  const [lo, hi] = range;
  if (min === max) {
    const mid = (lo + hi) / 2;
    return (v) => (v === null ? fallback : mid);
  }
  return (v) => (v === null ? fallback : lo + ((v - min) / (max - min)) * (hi - lo));
}

/** Visible in/out degree per node id (spec "Decisions": the canvas is the working set). */
export function visibleDegrees(edges: CanvasEdge[]): Map<number, { in: number; out: number }> {
  const degrees = new Map<number, { in: number; out: number }>();
  const at = (id: number) => {
    let d = degrees.get(id);
    if (!d) {
      d = { in: 0, out: 0 };
      degrees.set(id, d);
    }
    return d;
  };
  for (const e of edges) {
    at(e.source).out++;
    at(e.target).in++;
  }
  return degrees;
}

export function resolveStyles(
  nodes: Record<number, CanvasNode>,
  edges: Record<number, CanvasEdge>,
  overlay: PathOverlaySets,
  config: StyleConfig,
): ResolvedStyles {
  const nodeList = Object.values(nodes);
  const edgeList = Object.values(edges);

  const nodeColorScale = buildColorScale(nodeList, config.nodeColorMode, config.nodeColorProperty);
  const edgeColorScale = buildColorScale(edgeList, config.edgeColorMode, config.edgeColorProperty);

  const degrees =
    config.nodeSizeMode === "in-degree" ||
    config.nodeSizeMode === "out-degree" ||
    config.nodeSizeMode === "degree"
      ? visibleDegrees(edgeList)
      : null;

  const nodeSizeSource = (node: CanvasNode): number | null => {
    switch (config.nodeSizeMode) {
      case "property":
        return config.nodeSizeProperty
          ? numericValue(propValue(node, config.nodeSizeProperty))
          : null;
      case "in-degree":
        return degrees!.get(node.id)?.in ?? 0;
      case "out-degree":
        return degrees!.get(node.id)?.out ?? 0;
      case "degree": {
        const d = degrees!.get(node.id);
        return (d?.in ?? 0) + (d?.out ?? 0);
      }
      default:
        return null;
    }
  };
  const nodeSize =
    config.nodeSizeMode === "fixed"
      ? () => NODE_SIZE_DEFAULT
      : scaleInto(nodeList.map(nodeSizeSource), NODE_SIZE_RANGE, NODE_SIZE_DEFAULT);

  const edgeWidthSource = (edge: CanvasEdge): number | null =>
    config.edgeWidthMode === "property" && config.edgeWidthProperty
      ? numericValue(propValue(edge, config.edgeWidthProperty))
      : null;
  const edgeWidth =
    config.edgeWidthMode === "fixed"
      ? () => EDGE_WIDTH_DEFAULT
      : scaleInto(edgeList.map(edgeWidthSource), EDGE_WIDTH_RANGE, EDGE_WIDTH_DEFAULT);

  const resolvedNodes: Record<number, ResolvedNodeStyle> = {};
  for (const node of nodeList) {
    const inPath = overlay.nodeIds.has(node.id);
    const dimmed = overlay.active && overlay.dim && !inPath;
    const size = nodeSize(nodeSizeSource(node));
    const image = config.nodeImageProperty
      ? classifyImageValue(propValue(node, config.nodeImageProperty))
      : null;
    resolvedNodes[node.id] = {
      color: dimmed ? DIM_NODE_COLOR : colorFromScale(nodeColorScale, node, colorForLabel(node.label)),
      size: inPath ? Math.max(size, PATH_NODE_MIN_SIZE) : size,
      // A dimmed image would still pop against the dim palette — suppress it (FR-9).
      image: dimmed ? null : image,
      zIndex: inPath ? 2 : 1,
      dimmed,
    };
  }

  const resolvedEdges: Record<number, ResolvedEdgeStyle> = {};
  for (const edge of edgeList) {
    const inPath = overlay.edgeIds.has(edge.id);
    const dimmed = overlay.active && overlay.dim && !inPath;
    const width = edgeWidth(edgeWidthSource(edge));
    const labelFallback = edge.label ? colorForLabel(edge.label) : UNLABELED_EDGE_COLOR;
    resolvedEdges[edge.id] = {
      color: inPath ? PATH_EDGE_COLOR : dimmed ? DIM_EDGE_COLOR : colorFromScale(edgeColorScale, edge, labelFallback),
      width: inPath ? Math.max(width, PATH_EDGE_MIN_WIDTH) : width,
      zIndex: inPath ? 2 : 0,
      dimmed,
    };
  }

  return { nodes: resolvedNodes, edges: resolvedEdges };
}

/** Legend model for the canvas screen (FR-10): follows the node color mode. */
export type LegendModel =
  | { kind: "categorical"; title: string; entries: { key: string; color: string; count: number }[] }
  | { kind: "gradient"; title: string; min: number; max: number };

export const LEGEND_MAX_ENTRIES = 12;

export function buildLegend(nodes: Record<number, CanvasNode>, config: StyleConfig): LegendModel {
  const nodeList = Object.values(nodes);
  const scale = buildColorScale(nodeList, config.nodeColorMode, config.nodeColorProperty);

  if (scale.kind === "gradient") {
    return { kind: "gradient", title: scale.property, min: scale.min, max: scale.max };
  }

  const counts = new Map<string, { color: string; count: number }>();
  for (const node of nodeList) {
    let key: string;
    let color: string;
    if (scale.kind === "categorical") {
      const value = propValue(node, scale.property);
      key = value === undefined || value === "" ? "(missing)" : String(value);
      color = value === undefined || value === "" ? UNLABELED_COLOR : colorForValue(value);
    } else {
      key = node.label ?? "(unlabeled)";
      color = colorForLabel(node.label);
    }
    const entry = counts.get(key);
    if (entry) entry.count++;
    else counts.set(key, { color, count: 1 });
  }
  const entries = [...counts.entries()]
    .map(([key, { color, count }]) => ({ key, color, count }))
    .sort((a, b) => b.count - a.count)
    .slice(0, LEGEND_MAX_ENTRIES);
  return {
    kind: "categorical",
    title: scale.kind === "categorical" ? scale.property : "labels",
    entries,
  };
}

/** Sorted union of snapshot property keys — feeds the style panel datalists (FR-8). */
export function knownPropertyKeys(elements: StyledElement[]): string[] {
  const keys = new Set<string>();
  for (const el of elements) {
    for (const key of Object.keys(el.props ?? {})) keys.add(key);
  }
  return [...keys].sort();
}
