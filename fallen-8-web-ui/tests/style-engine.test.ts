// MIT License
//
// style-engine.test.ts
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
  buildColorScale,
  buildLegend,
  classifyImageValue,
  DIM_EDGE_COLOR,
  DIM_NODE_COLOR,
  EDGE_LABEL_SIZE_DEFAULT,
  EDGE_WIDTH_DEFAULT,
  EDGE_WIDTH_RANGE,
  EMPTY_OVERLAY,
  isUsableMagnitude,
  LABEL_SIZE_DEFAULT,
  resolveMagnitudes,
  knownPropertyKeys,
  NODE_SIZE_DEFAULT,
  NODE_SIZE_RANGE,
  numericValue,
  PATH_EDGE_COLOR,
  PATH_EDGE_MIN_WIDTH,
  PATH_NODE_MIN_SIZE,
  resolveStyles,
  UNLABELED_EDGE_COLOR,
  visibleDegrees,
  type PathOverlaySets,
} from "../src/canvas/styleEngine";
import {
  colorForLabel,
  colorForValue,
  gradientColor,
  GRADIENT_HIGH,
  GRADIENT_LOW,
  UNLABELED_COLOR,
} from "../src/canvas/styling";
import { DEFAULT_STYLE_CONFIG, type StyleConfig } from "../src/canvas/styleConfig";
import { sizeToVal, worldRadiusForVal } from "../src/canvas/eclipse";
import {
  CANVAS_PROP_MAX_STRING,
  snapshotProps,
  type CanvasEdge,
  type CanvasNode,
} from "../src/state/instanceStore";

function nodesOf(...list: CanvasNode[]): Record<number, CanvasNode> {
  return Object.fromEntries(list.map((n) => [n.id, n]));
}

function edgesOf(...list: CanvasEdge[]): Record<number, CanvasEdge> {
  return Object.fromEntries(list.map((e) => [e.id, e]));
}

function edge(id: number, source: number, target: number, props?: CanvasEdge["props"]): CanvasEdge {
  return { id, source, target, edgePropertyId: "knows", label: "knows", props };
}

function config(patch: Partial<StyleConfig>): StyleConfig {
  return { ...DEFAULT_STYLE_CONFIG, ...patch };
}

describe("numericValue", () => {
  it("accepts numbers and numeric strings, rejects text and booleans", () => {
    expect(numericValue(3.5)).toBe(3.5);
    expect(numericValue("42")).toBe(42);
    expect(numericValue("4.5e2")).toBe(450);
    expect(numericValue("abc")).toBeNull();
    expect(numericValue("")).toBeNull();
    expect(numericValue(true)).toBeNull();
    expect(numericValue(undefined)).toBeNull();
    expect(numericValue(Number.NaN)).toBeNull();
  });
});

describe("classifyImageValue (FR-5)", () => {
  it("recognizes http(s) and data URLs", () => {
    expect(classifyImageValue("https://x.test/a.png")).toEqual({
      kind: "url",
      value: "https://x.test/a.png",
    });
    expect(classifyImageValue("HTTP://x.test/b.jpg")?.kind).toBe("url");
    expect(classifyImageValue("data:image/png;base64,AAAA")?.kind).toBe("url");
  });

  it("treats any other scalar as emoji/text and caps at 8 code points", () => {
    expect(classifyImageValue("🦊")).toEqual({ kind: "emoji", value: "🦊" });
    expect(classifyImageValue(" 42 ")).toEqual({ kind: "emoji", value: "42" });
    const capped = classifyImageValue("abcdefghijkl");
    expect(capped).toEqual({ kind: "emoji", value: "abcdefgh" });
  });

  it("yields nothing for empty, boolean, or missing values", () => {
    expect(classifyImageValue("")).toBeNull();
    expect(classifyImageValue("   ")).toBeNull();
    expect(classifyImageValue(true)).toBeNull();
    expect(classifyImageValue(undefined)).toBeNull();
    expect(classifyImageValue(null)).toBeNull();
  });
});

describe("buildColorScale (FR-1/FR-2)", () => {
  const elements = [
    { label: "a", props: { score: 1 } },
    { label: "b", props: { score: "3" } },
  ];

  it("falls back to label mode without a property", () => {
    expect(buildColorScale(elements, "label", "")).toEqual({ kind: "label" });
    expect(buildColorScale(elements, "property", "")).toEqual({ kind: "label" });
  });

  it("is a gradient when every present value is numeric (numeric strings count)", () => {
    expect(buildColorScale(elements, "property", "score")).toEqual({
      kind: "gradient",
      property: "score",
      min: 1,
      max: 3,
    });
  });

  it("is categorical when any value is non-numeric or no values exist", () => {
    const mixed = [...elements, { label: "c", props: { score: "high" } }];
    expect(buildColorScale(mixed, "property", "score").kind).toBe("categorical");
    expect(buildColorScale([{ label: "x", props: {} }], "property", "score").kind).toBe(
      "categorical",
    );
  });
});

describe("resolveStyles — defaults reproduce the pre-feature canvas", () => {
  it("uses label colors, fixed sizes, fixed widths", () => {
    const nodes = nodesOf(
      { id: 1, label: "person", props: {} },
      { id: 2, label: null, props: {} },
    );
    const edges = edgesOf(edge(10, 1, 2));
    const styles = resolveStyles(nodes, edges, EMPTY_OVERLAY, DEFAULT_STYLE_CONFIG);

    expect(styles.nodes[1]).toMatchObject({
      color: colorForLabel("person"),
      size: NODE_SIZE_DEFAULT,
      image: null,
      zIndex: 1,
      dimmed: false,
    });
    expect(styles.nodes[2].color).toBe(UNLABELED_COLOR);
    expect(styles.edges[10]).toMatchObject({
      color: colorForLabel("knows"),
      width: EDGE_WIDTH_DEFAULT,
      zIndex: 0,
      dimmed: false,
    });
  });

  it("colors unlabeled edges with the muted edge fallback", () => {
    const nodes = nodesOf({ id: 1, label: null }, { id: 2, label: null });
    const edges = edgesOf({ id: 10, source: 1, target: 2, edgePropertyId: null, label: null });
    const styles = resolveStyles(nodes, edges, EMPTY_OVERLAY, DEFAULT_STYLE_CONFIG);
    expect(styles.edges[10].color).toBe(UNLABELED_EDGE_COLOR);
  });

  it("tolerates nodes persisted before props existed", () => {
    const nodes = nodesOf({ id: 1, label: "person" });
    const styles = resolveStyles(
      nodes,
      {},
      EMPTY_OVERLAY,
      config({ nodeColorMode: "property", nodeColorProperty: "x", nodeSizeMode: "property", nodeSizeProperty: "y", nodeImageProperty: "z" }),
    );
    expect(styles.nodes[1].color).toBe(UNLABELED_COLOR);
    expect(styles.nodes[1].size).toBe(NODE_SIZE_DEFAULT);
    expect(styles.nodes[1].image).toBeNull();
  });
});

describe("resolveStyles — color by property (FR-1)", () => {
  it("hashes categorical values stably and marks missing values unlabeled", () => {
    const nodes = nodesOf(
      { id: 1, label: "a", props: { team: "red" } },
      { id: 2, label: "b", props: { team: "red" } },
      { id: 3, label: "c", props: { team: "blue" } },
      { id: 4, label: "d", props: {} },
    );
    const styles = resolveStyles(
      nodes,
      {},
      EMPTY_OVERLAY,
      config({ nodeColorMode: "property", nodeColorProperty: "team" }),
    );
    expect(styles.nodes[1].color).toBe(styles.nodes[2].color);
    expect(styles.nodes[1].color).toBe(colorForValue("red"));
    expect(styles.nodes[3].color).toBe(colorForValue("blue"));
    expect(styles.nodes[4].color).toBe(UNLABELED_COLOR);
  });

  it("shades all-numeric values min→max along the gradient", () => {
    const nodes = nodesOf(
      { id: 1, label: "a", props: { score: 0 } },
      { id: 2, label: "b", props: { score: 5 } },
      { id: 3, label: "c", props: { score: 10 } },
    );
    const styles = resolveStyles(
      nodes,
      {},
      EMPTY_OVERLAY,
      config({ nodeColorMode: "property", nodeColorProperty: "score" }),
    );
    expect(styles.nodes[1].color).toBe(GRADIENT_LOW);
    expect(styles.nodes[2].color).toBe(gradientColor(0.5));
    expect(styles.nodes[3].color).toBe(GRADIENT_HIGH);
  });

  it("uses the gradient midpoint when all values are equal", () => {
    const nodes = nodesOf(
      { id: 1, label: "a", props: { score: 7 } },
      { id: 2, label: "b", props: { score: 7 } },
    );
    const styles = resolveStyles(
      nodes,
      {},
      EMPTY_OVERLAY,
      config({ nodeColorMode: "property", nodeColorProperty: "score" }),
    );
    expect(styles.nodes[1].color).toBe(gradientColor(0.5));
  });
});

describe("resolveStyles — node size (FR-3)", () => {
  it("min-max scales numeric properties into the size range", () => {
    const nodes = nodesOf(
      { id: 1, label: "a", props: { w: 10 } },
      { id: 2, label: "b", props: { w: 20 } },
      { id: 3, label: "c", props: {} },
    );
    const styles = resolveStyles(
      nodes,
      {},
      EMPTY_OVERLAY,
      config({ nodeSizeMode: "property", nodeSizeProperty: "w" }),
    );
    expect(styles.nodes[1].size).toBe(NODE_SIZE_RANGE[0]);
    expect(styles.nodes[2].size).toBe(NODE_SIZE_RANGE[1]);
    expect(styles.nodes[3].size).toBe(NODE_SIZE_DEFAULT);
  });

  it("sizes by visible in/out/total degree", () => {
    // 1 -> 2, 1 -> 3, 2 -> 3
    const nodes = nodesOf(
      { id: 1, label: "a" },
      { id: 2, label: "b" },
      { id: 3, label: "c" },
    );
    const edges = edgesOf(edge(10, 1, 2), edge(11, 1, 3), edge(12, 2, 3));

    const outSized = resolveStyles(nodes, edges, EMPTY_OVERLAY, config({ nodeSizeMode: "out-degree" }));
    expect(outSized.nodes[1].size).toBe(NODE_SIZE_RANGE[1]); // out 2
    expect(outSized.nodes[3].size).toBe(NODE_SIZE_RANGE[0]); // out 0

    const inSized = resolveStyles(nodes, edges, EMPTY_OVERLAY, config({ nodeSizeMode: "in-degree" }));
    expect(inSized.nodes[3].size).toBe(NODE_SIZE_RANGE[1]); // in 2
    expect(inSized.nodes[1].size).toBe(NODE_SIZE_RANGE[0]); // in 0

    const totalSized = resolveStyles(nodes, edges, EMPTY_OVERLAY, config({ nodeSizeMode: "degree" }));
    // total degrees: 1 → 2, 2 → 2, 3 → 2 ⇒ all equal ⇒ midpoint of the range
    expect(totalSized.nodes[1].size).toBe((NODE_SIZE_RANGE[0] + NODE_SIZE_RANGE[1]) / 2);
  });
});

describe("resolveStyles — edge width (FR-4)", () => {
  it("min-max scales numeric properties and defaults the rest", () => {
    const nodes = nodesOf({ id: 1, label: "a" }, { id: 2, label: "b" });
    const edges = edgesOf(
      edge(10, 1, 2, { weight: 1 }),
      edge(11, 1, 2, { weight: 9 }),
      edge(12, 1, 2, { weight: "oops" }),
    );
    const styles = resolveStyles(
      nodes,
      edges,
      EMPTY_OVERLAY,
      config({ edgeWidthMode: "property", edgeWidthProperty: "weight" }),
    );
    expect(styles.edges[10].width).toBe(EDGE_WIDTH_RANGE[0]);
    expect(styles.edges[11].width).toBe(EDGE_WIDTH_RANGE[1]);
    expect(styles.edges[12].width).toBe(EDGE_WIDTH_DEFAULT);
  });
});

describe("resolveStyles — node images (FR-5)", () => {
  it("resolves urls and emoji per node and only when configured", () => {
    const nodes = nodesOf(
      { id: 1, label: "a", props: { icon: "https://x.test/a.png" } },
      { id: 2, label: "b", props: { icon: "🦊" } },
      { id: 3, label: "c", props: {} },
    );
    const off = resolveStyles(nodes, {}, EMPTY_OVERLAY, DEFAULT_STYLE_CONFIG);
    expect(off.nodes[1].image).toBeNull();

    const on = resolveStyles(nodes, {}, EMPTY_OVERLAY, config({ nodeImageProperty: "icon" }));
    expect(on.nodes[1].image).toEqual({ kind: "url", value: "https://x.test/a.png" });
    expect(on.nodes[2].image).toEqual({ kind: "emoji", value: "🦊" });
    expect(on.nodes[3].image).toBeNull();
  });
});

describe("resolveStyles — path overlay precedence (FR-9)", () => {
  const nodes = nodesOf(
    { id: 1, label: "a", props: { icon: "🦊" } },
    { id: 2, label: "b", props: { icon: "🦊" } },
  );
  const edges = edgesOf(edge(10, 1, 2), edge(11, 2, 1));
  const overlay: PathOverlaySets = {
    nodeIds: new Set([1]),
    edgeIds: new Set([10]),
    active: true,
    dim: true,
  };

  it("dims non-path elements and suppresses their images", () => {
    const styles = resolveStyles(nodes, edges, overlay, config({ nodeImageProperty: "icon" }));
    expect(styles.nodes[2]).toMatchObject({ color: DIM_NODE_COLOR, image: null, dimmed: true });
    expect(styles.edges[11]).toMatchObject({ color: DIM_EDGE_COLOR, dimmed: true });
  });

  it("highlights path members: styled color kept, size/width floored, raised zIndex", () => {
    const styles = resolveStyles(nodes, edges, overlay, config({ nodeImageProperty: "icon" }));
    expect(styles.nodes[1].color).toBe(colorForLabel("a"));
    expect(styles.nodes[1].size).toBeGreaterThanOrEqual(PATH_NODE_MIN_SIZE);
    expect(styles.nodes[1].image).toEqual({ kind: "emoji", value: "🦊" });
    expect(styles.nodes[1].zIndex).toBe(2);
    expect(styles.edges[10]).toMatchObject({
      color: PATH_EDGE_COLOR,
      width: PATH_EDGE_MIN_WIDTH,
      zIndex: 2,
    });
  });
});

describe("resolveStyles — non-dimming emphasis (adjacency-preview)", () => {
  const nodes = nodesOf(
    { id: 1, label: "a", props: { icon: "🦊" } },
    { id: 2, label: "b", props: { icon: "🦊" } },
  );
  const edges = edgesOf(edge(10, 1, 2), edge(11, 2, 1));
  const emphasis: PathOverlaySets = {
    nodeIds: new Set([1]),
    edgeIds: new Set([10]),
    active: true,
    dim: false,
  };

  it("keeps non-members fully styled — colors, images, no dimming", () => {
    const styles = resolveStyles(nodes, edges, emphasis, config({ nodeImageProperty: "icon" }));
    expect(styles.nodes[2]).toMatchObject({
      color: colorForLabel("b"),
      image: { kind: "emoji", value: "🦊" },
      dimmed: false,
    });
    expect(styles.edges[11]).toMatchObject({ color: colorForLabel("knows"), dimmed: false });
  });

  it("still pops the emphasized members like the path overlay does", () => {
    const styles = resolveStyles(nodes, edges, emphasis, DEFAULT_STYLE_CONFIG);
    expect(styles.nodes[1].size).toBeGreaterThanOrEqual(PATH_NODE_MIN_SIZE);
    expect(styles.nodes[1].zIndex).toBe(2);
    expect(styles.edges[10]).toMatchObject({
      color: PATH_EDGE_COLOR,
      width: PATH_EDGE_MIN_WIDTH,
      zIndex: 2,
    });
  });
});

describe("visibleDegrees", () => {
  it("counts per direction over the canvas edges", () => {
    const degrees = visibleDegrees([edge(1, 1, 2), edge(2, 1, 3), edge(3, 3, 1)]);
    expect(degrees.get(1)).toEqual({ in: 1, out: 2 });
    expect(degrees.get(2)).toEqual({ in: 1, out: 0 });
    expect(degrees.get(3)).toEqual({ in: 1, out: 1 });
  });
});

describe("buildLegend (FR-10)", () => {
  const nodes = nodesOf(
    { id: 1, label: "person", props: { team: "red", score: 1 } },
    { id: 2, label: "person", props: { team: "red", score: 2 } },
    { id: 3, label: "city", props: { score: 3 } },
  );

  it("lists labels with counts in label mode", () => {
    const legend = buildLegend(nodes, DEFAULT_STYLE_CONFIG);
    expect(legend).toMatchObject({ kind: "categorical", title: "labels" });
    if (legend.kind !== "categorical") throw new Error("unreachable");
    expect(legend.entries[0]).toEqual({ key: "person", color: colorForLabel("person"), count: 2 });
  });

  it("lists property values (missing bucketed) in categorical property mode", () => {
    const legend = buildLegend(
      nodes,
      config({ nodeColorMode: "property", nodeColorProperty: "team" }),
    );
    if (legend.kind !== "categorical") throw new Error("expected categorical");
    expect(legend.title).toBe("team");
    expect(legend.entries).toContainEqual({ key: "red", color: colorForValue("red"), count: 2 });
    expect(legend.entries).toContainEqual({ key: "(missing)", color: UNLABELED_COLOR, count: 1 });
  });

  it("reports min/max for numeric properties", () => {
    const legend = buildLegend(
      nodes,
      config({ nodeColorMode: "property", nodeColorProperty: "score" }),
    );
    expect(legend).toEqual({ kind: "gradient", title: "score", min: 1, max: 3 });
  });
});

describe("knownPropertyKeys", () => {
  it("returns the sorted union, tolerating missing props", () => {
    expect(
      knownPropertyKeys([
        { label: null, props: { b: 1, a: "x" } },
        { label: null, props: { c: true } },
        { label: null },
      ]),
    ).toEqual(["a", "b", "c"]);
  });
});

describe("snapshotProps (FR-11)", () => {
  it("keeps scalars, drops arrays/objects/null, caps long strings", () => {
    const props = snapshotProps([
      { propertyId: "name", propertyValue: "Ada" },
      { propertyId: "age", propertyValue: 42 },
      { propertyId: "active", propertyValue: true },
      { propertyId: "embedding", propertyValue: [0.1, 0.2] },
      { propertyId: "nested", propertyValue: { a: 1 } },
      { propertyId: "nothing", propertyValue: null },
      { propertyId: "long", propertyValue: "x".repeat(CANVAS_PROP_MAX_STRING + 50) },
    ]);
    expect(props).toMatchObject({ name: "Ada", age: 42, active: true });
    expect(props.embedding).toBeUndefined();
    expect(props.nested).toBeUndefined();
    expect(props.nothing).toBeUndefined();
    expect((props.long as string).length).toBe(CANVAS_PROP_MAX_STRING);
  });

  it("handles absent property lists", () => {
    expect(snapshotProps(null)).toEqual({});
    expect(snapshotProps(undefined)).toEqual({});
  });
});


/**
 * Configurable magnitudes (feature canvas-host-controls). Two guarantees are load-bearing and get
 * the most attention here: omitting every new field must render EXACTLY as the feature found it,
 * and the path-overlay minimums must keep winning whatever a host configures, so a highlighted path
 * can never become invisible.
 */

/** The same graph the pre-change resolved sizes were captured from, so the numbers below transfer. */
const MAGNITUDE_NODES = nodesOf(
  { id: 1, label: "a", props: { w: 1 } },
  { id: 2, label: "b", props: { w: 5 } },
  { id: 3, label: "b", props: { w: 9 } },
  { id: 4, label: null },
);
const MAGNITUDE_EDGES = edgesOf(
  { id: 10, source: 1, target: 2, edgePropertyId: "k", label: "k", props: { w: 2 } },
  { id: 11, source: 2, target: 3, edgePropertyId: "k", label: "k", props: { w: 8 } },
  { id: 12, source: 3, target: 1, edgePropertyId: "k", label: "k" },
);
const MAGNITUDE_OVERLAY: PathOverlaySets = {
  nodeIds: new Set([1, 2]),
  edgeIds: new Set([10]),
  active: true,
  dim: true,
};

const PROPERTY_SCALED: Partial<StyleConfig> = {
  nodeSizeMode: "property",
  nodeSizeProperty: "w",
  edgeWidthMode: "property",
  edgeWidthProperty: "w",
};

function sizesOf(patch: Partial<StyleConfig>, overlay: PathOverlaySets = EMPTY_OVERLAY) {
  const resolved = resolveStyles(MAGNITUDE_NODES, MAGNITUDE_EDGES, overlay, config(patch));
  return {
    nodes: Object.fromEntries(Object.entries(resolved.nodes).map(([id, n]) => [id, n.size])),
    edges: Object.fromEntries(Object.entries(resolved.edges).map(([id, e]) => [id, e.width])),
  };
}

describe("resolveMagnitudes", () => {
  it("resolves an unconfigured config to exactly the exported defaults", () => {
    // The exported constants ARE the documented defaults (hosts import them to compute their own
    // magnitudes from), so this is the contract, not an implementation detail.
    expect(resolveMagnitudes(DEFAULT_STYLE_CONFIG)).toEqual({
      nodeSize: NODE_SIZE_DEFAULT,
      nodeSizeRange: NODE_SIZE_RANGE,
      nodeSizeFallback: NODE_SIZE_DEFAULT,
      edgeWidth: EDGE_WIDTH_DEFAULT,
      edgeWidthRange: EDGE_WIDTH_RANGE,
      edgeWidthFallback: EDGE_WIDTH_DEFAULT,
      labelSize: LABEL_SIZE_DEFAULT,
      edgeLabelSize: EDGE_LABEL_SIZE_DEFAULT,
    });
  });

  it("passes a configured magnitude through untouched", () => {
    const resolved = resolveMagnitudes(
      config({ nodeSize: 20, nodeSizeRange: [10, 40], edgeWidth: 3, edgeWidthRange: [1, 9], labelSize: 22, edgeLabelSize: 18 }),
    );
    expect(resolved).toEqual({
      nodeSize: 20,
      nodeSizeRange: [10, 40],
      nodeSizeFallback: 20,
      edgeWidth: 3,
      edgeWidthRange: [1, 9],
      edgeWidthFallback: 3,
      labelSize: 22,
      edgeLabelSize: 18,
    });
  });

  it("falls back for every value that would render nothing or divide by zero", () => {
    // A host reaches this from plain JavaScript, where 0, -1 and NaN all arrive without a type
    // error. 0 matters most: sigma draws no node at all, while 3d-force-graph reads a falsy nodeVal
    // as 1 and draws a DEFAULT-sized sphere, so honouring it would split the two renderers.
    for (const bad of [0, -4, Number.NaN, Number.POSITIVE_INFINITY] as number[]) {
      const resolved = resolveMagnitudes(
        config({ nodeSize: bad, edgeWidth: bad, labelSize: bad, edgeLabelSize: bad }),
      );
      expect(resolved.nodeSize).toBe(NODE_SIZE_DEFAULT);
      expect(resolved.edgeWidth).toBe(EDGE_WIDTH_DEFAULT);
      expect(resolved.labelSize).toBe(LABEL_SIZE_DEFAULT);
      expect(resolved.edgeLabelSize).toBe(EDGE_LABEL_SIZE_DEFAULT);
    }
  });

  it("rejects a range as a whole when either end is unusable", () => {
    // Half a range has no sane reading, so it falls back rather than mixing a host's number with a
    // default and scaling into something neither party asked for.
    for (const bad of [[0, 20], [20, 0], [Number.NaN, 20], [-5, 5]] as [number, number][]) {
      expect(resolveMagnitudes(config({ nodeSizeRange: bad })).nodeSizeRange).toBe(NODE_SIZE_RANGE);
      expect(resolveMagnitudes(config({ edgeWidthRange: bad })).edgeWidthRange).toBe(EDGE_WIDTH_RANGE);
    }
  });

  it("keeps an inverted range inverted rather than quietly sorting it", () => {
    // Larger property value, smaller element is a legitimate thing to ask for; reordering would
    // invert the host's intent behind their back.
    expect(resolveMagnitudes(config({ nodeSizeRange: [30, 6] })).nodeSizeRange).toEqual([30, 6]);
  });

  it("is what resolveStyles hangs on its result, so the renderers never default again", () => {
    const resolved = resolveStyles(MAGNITUDE_NODES, MAGNITUDE_EDGES, EMPTY_OVERLAY, config({ labelSize: 17 }));
    expect(resolved.magnitudes).toEqual(resolveMagnitudes(config({ labelSize: 17 })));
    expect(resolved.magnitudes.labelSize).toBe(17);
    expect(resolved.magnitudes.edgeLabelSize).toBe(EDGE_LABEL_SIZE_DEFAULT);
  });
});

describe("isUsableMagnitude", () => {
  it("accepts only finite numbers above zero", () => {
    expect(isUsableMagnitude(0.5)).toBe(true);
    expect(isUsableMagnitude(14)).toBe(true);
    expect(isUsableMagnitude(0)).toBe(false);
    expect(isUsableMagnitude(-1)).toBe(false);
    expect(isUsableMagnitude(Number.NaN)).toBe(false);
    expect(isUsableMagnitude(Number.POSITIVE_INFINITY)).toBe(false);
    expect(isUsableMagnitude(undefined)).toBe(false);
  });
});

describe("magnitudes: omitting every new field changes nothing", () => {
  /**
   * Captured by RUNNING the pre-feature resolveStyles over the graph above, not written by hand
   * from the formulas, so this is a genuine before/after comparison rather than a restatement of
   * the implementation. Every size mode plus the path overlay, because the overlay clamps and the
   * scaled modes take a different code path from the fixed one.
   */
  const BEFORE: Record<string, { nodes: Record<string, number>; edges: Record<string, number> }> = {
    fixed: { nodes: { 1: 5, 2: 5, 3: 5, 4: 5 }, edges: { 10: 1, 11: 1, 12: 1 } },
    "prop-size": { nodes: { 1: 3, 2: 8.5, 3: 14, 4: 5 }, edges: { 10: 0.5, 11: 5, 12: 1 } },
    degree: { nodes: { 1: 14, 2: 14, 3: 14, 4: 3 }, edges: { 10: 1, 11: 1, 12: 1 } },
    "in-degree": { nodes: { 1: 14, 2: 14, 3: 14, 4: 3 }, edges: { 10: 1, 11: 1, 12: 1 } },
    "out-degree": { nodes: { 1: 14, 2: 14, 3: 14, 4: 3 }, edges: { 10: 1, 11: 1, 12: 1 } },
    "overlay-fixed": { nodes: { 1: 8, 2: 8, 3: 5, 4: 5 }, edges: { 10: 3, 11: 1, 12: 1 } },
    "overlay-prop": { nodes: { 1: 8, 2: 8.5, 3: 14, 4: 5 }, edges: { 10: 3, 11: 5, 12: 1 } },
  };

  const CASES: [string, Partial<StyleConfig>, PathOverlaySets][] = [
    ["fixed", {}, EMPTY_OVERLAY],
    ["prop-size", PROPERTY_SCALED, EMPTY_OVERLAY],
    ["degree", { nodeSizeMode: "degree" }, EMPTY_OVERLAY],
    ["in-degree", { nodeSizeMode: "in-degree" }, EMPTY_OVERLAY],
    ["out-degree", { nodeSizeMode: "out-degree" }, EMPTY_OVERLAY],
    ["overlay-fixed", {}, MAGNITUDE_OVERLAY],
    ["overlay-prop", PROPERTY_SCALED, MAGNITUDE_OVERLAY],
  ];

  for (const [name, patch, overlay] of CASES) {
    it(`reproduces the pre-feature sizes: ${name}`, () => {
      expect(sizesOf(patch, overlay)).toEqual(BEFORE[name]);
    });
  }

  it("also reproduces them when the fields are present but explicitly undefined", () => {
    // Spreading a partially-filled host config is how this actually arrives, and `undefined` must
    // read as "omitted" rather than falling through to NaN.
    expect(
      sizesOf({ nodeSize: undefined, nodeSizeRange: undefined, edgeWidth: undefined, edgeWidthRange: undefined }),
    ).toEqual(BEFORE.fixed);
  });
});

describe("magnitudes: a configured value reaches the resolved sizes", () => {
  it("uses the configured node size in fixed mode and the configured range when scaling", () => {
    expect(sizesOf({ nodeSize: 20 }).nodes).toEqual({ 1: 20, 2: 20, 3: 20, 4: 20 });
    // Property mode maps [1, 9] onto the configured range; node 4 has no property to measure.
    const scaled = sizesOf({ ...PROPERTY_SCALED, nodeSizeRange: [10, 50] }).nodes;
    expect(scaled[1]).toBe(10);
    expect(scaled[3]).toBe(50);
    expect(scaled[2]).toBe(30);
  });

  it("uses the configured edge width and range", () => {
    expect(sizesOf({ edgeWidth: 4 }).edges).toEqual({ 10: 4, 11: 4, 12: 4 });
    const scaled = sizesOf({ ...PROPERTY_SCALED, edgeWidthRange: [2, 12] }).edges;
    expect(scaled[10]).toBe(2);
    expect(scaled[11]).toBe(12);
  });

  it("pulls a DEFAULTED unmeasurable-element fallback inside a configured range", () => {
    // A host that configures ONLY a range would otherwise see its property-less nodes drawn at the
    // default 5 while every measurable one sits in [20, 40]: a scale the host never asked for.
    expect(sizesOf({ ...PROPERTY_SCALED, nodeSizeRange: [20, 40] }).nodes[4]).toBe(20);
    expect(sizesOf({ ...PROPERTY_SCALED, edgeWidthRange: [6, 9] }).edges[12]).toBe(6);
  });

  it("honours an EXPLICIT fallback even outside the range, because that is what the field says", () => {
    // nodeSize is documented as the unmeasurable-element fallback, so a host that names a number
    // gets that number. Clamping an explicit value would make the documented contract a lie, and
    // there would be no way for the host to find out why.
    expect(sizesOf({ ...PROPERTY_SCALED, nodeSize: 20 }).nodes[4]).toBe(20); // range is [3, 14]
    expect(sizesOf({ ...PROPERTY_SCALED, nodeSize: 1 }).nodes[4]).toBe(1);
    expect(sizesOf({ ...PROPERTY_SCALED, edgeWidth: 40 }).edges[12]).toBe(40); // range is [0.5, 5]
    // ...and it is still honoured when it happens to sit inside the range.
    expect(sizesOf({ ...PROPERTY_SCALED, nodeSize: 33, nodeSizeRange: [20, 40] }).nodes[4]).toBe(33);
  });

  it("carries a configured node size through to the 3D val, not just the 2D radius", () => {
    // The two renderers must move together. Resolve a real config, then push the resolved size
    // through the 3D anchor: a divisor taken from config.nodeSize would collapse val back to 1 here
    // and leave the 3D scene identical to the default while the 2D discs quadrupled.
    const size = resolveStyles(MAGNITUDE_NODES, MAGNITUDE_EDGES, EMPTY_OVERLAY, config({ nodeSize: 20 }))
      .nodes[1].size;
    expect(size).toBe(20);
    expect(sizeToVal(size)).toBe(64);
    expect(worldRadiusForVal(sizeToVal(size))).toBeCloseTo(16, 10);
    // ...and the omit-everything case still lands on val 1.
    const base = resolveStyles(MAGNITUDE_NODES, MAGNITUDE_EDGES, EMPTY_OVERLAY, DEFAULT_STYLE_CONFIG)
      .nodes[1].size;
    expect(sizeToVal(base)).toBe(1);
  });

  it("keeps every node inside the configured range when NOTHING can be measured", () => {
    // No element has the property, so there is no scale to place anyone on and they all take the
    // fallback. It is the clamped fallback, so the band the host configured still holds.
    expect(
      sizesOf({ nodeSizeMode: "property", nodeSizeProperty: "absent", nodeSizeRange: [20, 40] }).nodes,
    ).toEqual({ 1: 20, 2: 20, 3: 20, 4: 20 });
  });
});

describe("magnitudes: the path overlay minimums still win", () => {
  it("floors a highlighted path configured smaller than the minimums", () => {
    // The whole point of the floor: a host shrinking the graph for a dense view must not be able to
    // make the spotlighted path invisible.
    const tiny = sizesOf({ nodeSize: 1, edgeWidth: 0.1 }, MAGNITUDE_OVERLAY);
    expect(tiny.nodes[1]).toBe(PATH_NODE_MIN_SIZE);
    expect(tiny.nodes[2]).toBe(PATH_NODE_MIN_SIZE);
    expect(tiny.edges[10]).toBe(PATH_EDGE_MIN_WIDTH);
    // Off-path elements keep the configured magnitude, so the path still stands out.
    expect(tiny.nodes[3]).toBe(1);
    expect(tiny.edges[11]).toBe(0.1);
  });

  it("floors a scaled path element whose range puts it under the minimum", () => {
    const scaled = sizesOf({ ...PROPERTY_SCALED, nodeSizeRange: [1, 2], edgeWidthRange: [0.2, 0.4] }, MAGNITUDE_OVERLAY);
    expect(scaled.nodes[1]).toBe(PATH_NODE_MIN_SIZE);
    expect(scaled.edges[10]).toBe(PATH_EDGE_MIN_WIDTH);
  });

  it("never SHRINKS a path element that a host configured larger than the minimum", () => {
    const big = sizesOf({ nodeSize: 30, edgeWidth: 12 }, MAGNITUDE_OVERLAY);
    expect(big.nodes[1]).toBe(30);
    expect(big.edges[10]).toBe(12);
  });
});
