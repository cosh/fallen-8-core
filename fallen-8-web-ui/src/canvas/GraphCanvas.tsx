import { useEffect, useMemo, useRef } from "react";
import Graph from "graphology";
import Sigma from "sigma";
import { circular } from "graphology-layout";
import FA2Layout from "graphology-layout-forceatlas2/worker";
import forceAtlas2 from "graphology-layout-forceatlas2";
import type { CanvasEdge, CanvasNode } from "../state/instanceStore";
import type { PathREST } from "../api/types";
import { colorForLabel, DEGRADE_THRESHOLD, UNLABELED_COLOR } from "./styling";

export type ElementRef = { kind: "node" | "edge"; id: number };

/**
 * The one renderer boundary (design §4): everything outside talks CanvasNode/CanvasEdge +
 * callbacks; Sigma/graphology stay swappable behind this file.
 *
 * - layout "force" runs ForceAtlas2 in a Web Worker (never blocks input), "circular" is
 *   the deterministic alternative (FR-19).
 * - Path overlay (FR-14) highlights path elements against a dimmed neighborhood.
 * - Past DEGRADE_THRESHOLD rendered elements labels are dropped, not the frame rate (FR-20).
 */
export function GraphCanvas({
  nodes,
  edges,
  layout,
  pathOverlay,
  onSelect,
}: {
  nodes: Record<number, CanvasNode>;
  edges: Record<number, CanvasEdge>;
  layout: "force" | "circular";
  pathOverlay: PathREST | null;
  onSelect: (ref: ElementRef | null) => void;
}) {
  const containerRef = useRef<HTMLDivElement>(null);
  const sigmaRef = useRef<Sigma | null>(null);
  const fa2Ref = useRef<FA2Layout | null>(null);
  const graphRef = useRef<Graph>(new Graph({ multi: true, type: "directed" }));

  const overlay = useMemo(() => {
    const nodeIds = new Set<number>();
    const edgeIds = new Set<number>();
    if (pathOverlay) {
      for (const el of pathOverlay.pathElements) {
        nodeIds.add(el.sourceVertexId);
        nodeIds.add(el.targetVertexId);
        edgeIds.add(el.edgeId);
      }
    }
    return { nodeIds, edgeIds, active: pathOverlay !== null };
  }, [pathOverlay]);

  // Mount Sigma once.
  useEffect(() => {
    const container = containerRef.current;
    if (!container) return;

    const graph = graphRef.current;
    const sigma = new Sigma(graph, container, {
      allowInvalidContainer: true,
      renderEdgeLabels: true,
      labelColor: { color: "#cdd6e4" },
      labelFont: "JetBrains Mono, monospace",
      labelSize: 11,
      edgeLabelColor: { color: "#55647a" },
      edgeLabelSize: 9,
      defaultEdgeColor: "#232a35",
      zIndex: true,
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

  // Sync the store model into graphology (merge-only diffing).
  useEffect(() => {
    const graph = graphRef.current;
    const elementCount = Object.keys(nodes).length + Object.keys(edges).length;
    const showLabels = elementCount <= DEGRADE_THRESHOLD;

    const seenNodes = new Set<string>();
    for (const node of Object.values(nodes)) {
      const key = String(node.id);
      seenNodes.add(key);
      const inPath = overlay.nodeIds.has(node.id);
      const attributes = {
        label: showLabels ? (node.label ? `${node.label} #${node.id}` : `#${node.id}`) : null,
        color: overlay.active && !inPath ? "#2a3240" : colorForLabel(node.label),
        size: inPath ? 8 : 5,
        zIndex: inPath ? 2 : 1,
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
      const inPath = overlay.edgeIds.has(edge.id);
      const attributes = {
        elementId: edge.id,
        label: showLabels ? (edge.edgePropertyId ?? undefined) : undefined,
        color: overlay.active
          ? inPath
            ? "#4cc38a"
            : "#1a2029"
          : edge.label
            ? colorForLabel(edge.label)
            : "#2c3543",
        size: inPath ? 3 : 1,
        zIndex: inPath ? 2 : 0,
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

    sigmaRef.current?.refresh();
  }, [nodes, edges, overlay]);

  // Layout control: FA2 in a worker for "force", deterministic circular otherwise.
  useEffect(() => {
    const graph = graphRef.current;
    if (layout === "circular") {
      fa2Ref.current?.stop();
      circular.assign(graph, { scale: 100 });
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
  }, [layout, nodes, edges]);

  return (
    <div
      ref={containerRef}
      data-testid="graph-canvas"
      className="bg-ink h-full w-full"
      aria-label="graph canvas"
    />
  );
}

export { UNLABELED_COLOR };
