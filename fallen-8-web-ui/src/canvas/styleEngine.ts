// MIT License
//
// styleEngine.ts
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

import { edgeDisplayName, type CanvasEdge, type CanvasNode } from "../state/instanceStore";
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
  /** What the renderers draw with, defaulted once (see resolveMagnitudes). */
  magnitudes: ResolvedMagnitudes;
}

// Size/width ranges (FR-3/FR-4). Defaults match the pre-feature constants.
//
// These six are the DOCUMENTED DEFAULTS of the optional StyleConfig magnitudes, which is why
// they are exported all the way out to hosts (src/embed/canvas.ts): a host scaling the canvas
// to its own container needs to know what it is scaling from. Sigma sizes are absolute px, so
// a number here is a number of pixels at camera ratio 1.
export const NODE_SIZE_DEFAULT = 5;
export const NODE_SIZE_RANGE: readonly [number, number] = [3, 14];
export const EDGE_WIDTH_DEFAULT = 1;
export const EDGE_WIDTH_RANGE: readonly [number, number] = [0.5, 5];
export const LABEL_SIZE_DEFAULT = 11;
export const EDGE_LABEL_SIZE_DEFAULT = 9;

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

/**
 * The magnitudes a renderer actually draws with: each optional StyleConfig magnitude resolved
 * against its documented default. This is the ONE home for that defaulting, so no renderer and
 * no call site repeats a fallback expression.
 */
export interface ResolvedMagnitudes {
  nodeSize: number;
  nodeSizeRange: readonly [number, number];
  /** What a scaled mode draws for a node it cannot measure (see fallbackWithin). */
  nodeSizeFallback: number;
  edgeWidth: number;
  edgeWidthRange: readonly [number, number];
  edgeWidthFallback: number;
  labelSize: number;
  edgeLabelSize: number;
}

/**
 * The one rule for "a positive magnitude a host supplied": a finite number above zero. Exported
 * because the camera handle applies it to a zoom ratio for the same reason, namely that hosts call
 * in from plain JavaScript where `undefined`, `null` and NaN all arrive without a type error, and
 * every one of these numbers ends up a divisor in a renderer that does not validate it.
 *
 * Zero and negatives are refused rather than honoured: they render nothing, and a host that reaches
 * this state has a bug (a slider at its minimum, an unparsed input) that a blank canvas would hide.
 */
export function isUsableMagnitude(value: number | undefined): value is number {
  return typeof value === "number" && Number.isFinite(value) && value > 0;
}

function magnitude(value: number | undefined, fallback: number): number {
  return isUsableMagnitude(value) ? value : fallback;
}

/** Both endpoints must be usable or the whole range falls back: a half-valid range has no reading. */
function magnitudeRange(
  value: readonly [number, number] | undefined,
  fallback: readonly [number, number],
): readonly [number, number] {
  if (!value) return fallback;
  const [lo, hi] = value;
  // Deliberately NOT reordered when lo > hi. An inverted range is a legitimate host choice
  // (larger property value, smaller element), and quietly sorting it would invert their intent.
  return isUsableMagnitude(lo) && isUsableMagnitude(hi) ? value : fallback;
}

/** `value` brought inside `[min, max]` of a range that may be given either way round. */
function clampInto(value: number, range: readonly [number, number]): number {
  const [lo, hi] = range;
  return Math.min(Math.max(value, Math.min(lo, hi)), Math.max(lo, hi));
}

/**
 * What a scaled mode draws for an element it cannot measure (no such property, or a non-numeric
 * value). A scalar the host set EXPLICITLY is honoured as given, because that is exactly what the
 * field documents. A defaulted one is pulled into the configured range instead: a host that sets
 * only `nodeSizeRange: [20, 40]` would otherwise see its property-less nodes drawn at the default 5,
 * off the bottom of its own scale. With neither configured this is clamp(5, [3, 14]) = 5.
 */
function fallbackWithin(
  configured: number | undefined,
  resolved: number,
  range: readonly [number, number],
): number {
  return isUsableMagnitude(configured) ? resolved : clampInto(resolved, range);
}

export function resolveMagnitudes(config: StyleConfig): ResolvedMagnitudes {
  const nodeSize = magnitude(config.nodeSize, NODE_SIZE_DEFAULT);
  const nodeSizeRange = magnitudeRange(config.nodeSizeRange, NODE_SIZE_RANGE);
  const edgeWidth = magnitude(config.edgeWidth, EDGE_WIDTH_DEFAULT);
  const edgeWidthRange = magnitudeRange(config.edgeWidthRange, EDGE_WIDTH_RANGE);
  return {
    nodeSize,
    nodeSizeRange,
    nodeSizeFallback: fallbackWithin(config.nodeSize, nodeSize, nodeSizeRange),
    edgeWidth,
    edgeWidthRange,
    edgeWidthFallback: fallbackWithin(config.edgeWidth, edgeWidth, edgeWidthRange),
    labelSize: magnitude(config.labelSize, LABEL_SIZE_DEFAULT),
    edgeLabelSize: magnitude(config.edgeLabelSize, EDGE_LABEL_SIZE_DEFAULT),
  };
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
  const magnitudes = resolveMagnitudes(config);

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
      ? () => magnitudes.nodeSize
      : scaleInto(nodeList.map(nodeSizeSource), magnitudes.nodeSizeRange, magnitudes.nodeSizeFallback);

  const edgeWidthSource = (edge: CanvasEdge): number | null =>
    config.edgeWidthMode === "property" && config.edgeWidthProperty
      ? numericValue(propValue(edge, config.edgeWidthProperty))
      : null;
  const edgeWidth =
    config.edgeWidthMode === "fixed"
      ? () => magnitudes.edgeWidth
      : scaleInto(edgeList.map(edgeWidthSource), magnitudes.edgeWidthRange, magnitudes.edgeWidthFallback);

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
    const displayName = edgeDisplayName(edge);
    const labelFallback = displayName ? colorForLabel(displayName) : UNLABELED_EDGE_COLOR;
    resolvedEdges[edge.id] = {
      color: inPath ? PATH_EDGE_COLOR : dimmed ? DIM_EDGE_COLOR : colorFromScale(edgeColorScale, edge, labelFallback),
      width: inPath ? Math.max(width, PATH_EDGE_MIN_WIDTH) : width,
      zIndex: inPath ? 2 : 0,
      dimmed,
    };
  }

  return { nodes: resolvedNodes, edges: resolvedEdges, magnitudes };
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
