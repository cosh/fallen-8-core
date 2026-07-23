import { useEffect, useRef } from "react";
import Graph from "graphology";
import Sigma from "sigma";
import { EdgeArrowProgram, EdgeRectangleProgram, NodeCircleProgram } from "sigma/rendering";
import { createNodeImageProgram } from "@sigma/node-image";
import EdgeCurveProgram, {
  DEFAULT_EDGE_CURVATURE,
  EdgeCurvedArrowProgram,
  indexParallelEdgesIndex,
} from "@sigma/edge-curve";
import { circlepack, circular, random } from "graphology-layout";
import FA2Layout from "graphology-layout-forceatlas2/worker";
import forceAtlas2 from "graphology-layout-forceatlas2";
import type { CanvasEdge, CanvasNode } from "../state/instanceStore";
import type { StyleConfig } from "./styleConfig";
import type { ResolvedStyles } from "./styleEngine";
import { imageUrlFor } from "./imageAssets";
import { DEGRADE_THRESHOLD } from "./styling";
import type { ElementRef } from "./GraphCanvas";

/**
 * Curvature spread for the i-th of a parallel-edge bundle (the sigma.js parallel-edges
 * recipe): amplitude-damped so big bundles stay inside a readable fan.
 */
function parallelCurvature(index: number, maxIndex: number): number {
  if (index < 0) return -parallelCurvature(-index, maxIndex);
  const amplitude = 3.5;
  const maxCurvature = amplitude * (1 - Math.exp(-maxIndex / amplitude)) * DEFAULT_EDGE_CURVATURE;
  return maxIndex === 0 ? 0 : (maxCurvature * index) / maxIndex;
}

/**
 * Sigma.js (WebGL) 2D projection. Renders resolved styles only — the mapping rules
 * live in styleEngine.ts; the layout/label/arrow options come from the style config.
 */
export function Canvas2D({
  nodes,
  edges,
  styles,
  config,
  onSelect,
}: {
  nodes: Record<number, CanvasNode>;
  edges: Record<number, CanvasEdge>;
  styles: ResolvedStyles;
  config: StyleConfig;
  onSelect: (ref: ElementRef | null) => void;
}) {
  const containerRef = useRef<HTMLDivElement>(null);
  const sigmaRef = useRef<Sigma | null>(null);
  const fa2Ref = useRef<FA2Layout | null>(null);
  const graphRef = useRef<Graph>(new Graph({ multi: true, type: "directed" }));

  // Mount Sigma once.
  useEffect(() => {
    const container = containerRef.current;
    if (!container) return;

    const graph = graphRef.current;
    const sigma = new Sigma(graph, container, {
      allowInvalidContainer: true,
      // Off by default in Sigma v3 - without it clickEdge never fires, breaking edge
      // selection on the canvas and edge hops in the adjacency preview.
      enableEdgeEvents: true,
      // Width-1 edges render ~1px and the click hit area equals the rendered geometry -
      // a floor keeps them clickable without touching the resolved style widths.
      minEdgeThickness: 2.5,
      renderEdgeLabels: true,
      labelColor: { color: "#cdd6e4" },
      labelFont: "JetBrains Mono, monospace",
      labelSize: 11,
      edgeLabelColor: { color: "#55647a" },
      edgeLabelSize: 9,
      defaultEdgeColor: "#232a35",
      zIndex: true,
      nodeProgramClasses: {
        circle: NodeCircleProgram,
        image: createNodeImageProgram(),
      },
      edgeProgramClasses: {
        line: EdgeRectangleProgram,
        arrow: EdgeArrowProgram,
        curved: EdgeCurveProgram,
        curvedArrow: EdgeCurvedArrowProgram,
      },
    });
    sigmaRef.current = sigma;

    sigma.on("clickNode", ({ node }) => onSelect({ kind: "node", id: Number(node) }));
    sigma.on("clickEdge", ({ edge }) => {
      const id = graph.getEdgeAttribute(edge, "elementId") as number;
      onSelect({ kind: "edge", id });
    });
    sigma.on("clickStage", () => onSelect(null));

    return () => {
      fa2Ref.current?.kill();
      fa2Ref.current = null;
      sigma.kill();
      sigmaRef.current = null;
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  // Sync the store model + resolved styles into graphology (merge-only diffing).
  useEffect(() => {
    const graph = graphRef.current;
    const elementCount = Object.keys(nodes).length + Object.keys(edges).length;
    const showLabels = elementCount <= DEGRADE_THRESHOLD;

    const seenNodes = new Set<string>();
    for (const node of Object.values(nodes)) {
      const key = String(node.id);
      seenNodes.add(key);
      const style = styles.nodes[node.id];
      const image = style.image ? imageUrlFor(style.image) : null;
      const attributes = {
        label:
          showLabels && config.showNodeLabels
            ? node.label
              ? `${node.label} #${node.id}`
              : `#${node.id}`
            : null,
        color: style.color,
        size: style.size,
        zIndex: style.zIndex,
        type: image ? "image" : "circle",
        image: image ?? undefined,
        // Grouping key for the circlepack layout (display labels carry "#id" noise).
        group: node.label ?? "",
      };
      if (graph.hasNode(key)) {
        graph.mergeNodeAttributes(key, attributes);
      } else {
        graph.addNode(key, {
          ...attributes,
          x: Math.random() * 10 - 5,
          y: Math.random() * 10 - 5,
        });
      }
    }
    for (const nodeKey of graph.nodes()) {
      if (!seenNodes.has(nodeKey)) graph.dropNode(nodeKey);
    }

    const seenEdges = new Set<string>();
    for (const edge of Object.values(edges)) {
      const key = `e${edge.id}`;
      const source = String(edge.source);
      const target = String(edge.target);
      if (!graph.hasNode(source) || !graph.hasNode(target)) continue;
      seenEdges.add(key);
      const style = styles.edges[edge.id];
      const attributes = {
        elementId: edge.id,
        label: showLabels && config.showEdgeLabels ? (edge.edgePropertyId ?? undefined) : undefined,
        color: style.color,
        size: style.width,
        zIndex: style.zIndex,
      };
      if (graph.hasEdge(key)) {
        graph.mergeEdgeAttributes(key, attributes);
      } else {
        graph.addEdgeWithKey(key, source, target, attributes);
      }
    }
    for (const edgeKey of graph.edges()) {
      if (!seenEdges.has(edgeKey)) graph.dropEdge(edgeKey);
    }

    // Parallel edges (same endpoint pair, either direction) would render as coincident
    // straight lines - fan them out with spread curvatures instead.
    indexParallelEdgesIndex(graph);
    graph.forEachEdge((edgeKey, attributes) => {
      const parallelIndex = attributes.parallelIndex as number | null;
      const parallelMaxIndex = attributes.parallelMaxIndex as number | null;
      if (typeof parallelIndex === "number") {
        graph.mergeEdgeAttributes(edgeKey, {
          type: config.edgeArrows ? "curvedArrow" : "curved",
          curvature: parallelCurvature(parallelIndex, parallelMaxIndex ?? 1),
        });
      } else {
        graph.mergeEdgeAttributes(edgeKey, {
          type: config.edgeArrows ? "arrow" : "line",
          curvature: 0,
        });
      }
    });

    sigmaRef.current?.refresh();
  }, [nodes, edges, styles, config.showNodeLabels, config.showEdgeLabels, config.edgeArrows]);

  // Layout control (FR-6): FA2 in a worker for "force"; the rest are deterministic.
  useEffect(() => {
    const graph = graphRef.current;
    if (config.layout2d !== "force") {
      fa2Ref.current?.stop();
      if (graph.order > 0) {
        switch (config.layout2d) {
          case "circular":
            circular.assign(graph, { scale: 100 });
            break;
          case "circlepack":
            circlepack.assign(graph, { hierarchyAttributes: ["group"] });
            break;
          case "random":
            random.assign(graph, { scale: 100 });
            break;
          case "grid": {
            const keys = [...graph.nodes()].sort((a, b) => Number(a) - Number(b));
            const cols = Math.max(1, Math.ceil(Math.sqrt(keys.length)));
            keys.forEach((key, i) => {
              graph.setNodeAttribute(key, "x", (i % cols) * 10);
              graph.setNodeAttribute(key, "y", Math.floor(i / cols) * 10);
            });
            break;
          }
        }
      }
      sigmaRef.current?.refresh();
      return;
    }

    if (graph.order === 0) return;
    if (!fa2Ref.current) {
      const settings = forceAtlas2.inferSettings(graph);
      fa2Ref.current = new FA2Layout(graph, { settings });
    }
    fa2Ref.current.start();
    const stopTimer = window.setTimeout(() => fa2Ref.current?.stop(), 5_000);
    return () => window.clearTimeout(stopTimer);
  }, [config.layout2d, nodes, edges]);

  return (
    <div
      ref={containerRef}
      data-testid="graph-canvas"
      className="bg-ink h-full w-full"
      aria-label="graph canvas"
    />
  );
}
