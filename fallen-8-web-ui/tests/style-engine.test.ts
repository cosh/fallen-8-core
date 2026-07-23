import { describe, expect, it } from "vitest";
import {
  buildColorScale,
  buildLegend,
  classifyImageValue,
  DIM_EDGE_COLOR,
  DIM_NODE_COLOR,
  EDGE_WIDTH_DEFAULT,
  EDGE_WIDTH_RANGE,
  EMPTY_OVERLAY,
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
