import { lazy, Suspense, useMemo } from "react";
import type { CanvasEdge, CanvasNode } from "../state/instanceStore";
import type { PathREST } from "../api/types";
import type { StyleConfig } from "./styleConfig";
import { EMPTY_OVERLAY, resolveStyles, type PathOverlaySets } from "./styleEngine";
import { Canvas2D } from "./Canvas2D";

export type ElementRef = { kind: "node" | "edge"; id: number };

/** Elements to emphasize with the overlay visuals WITHOUT dimming the rest (adjacency-preview). */
type EmphasisSet = { nodeIds: readonly number[]; edgeIds: readonly number[] };

// three.js only loads when an instance actually switches to 3D.
const Canvas3D = lazy(() => import("./Canvas3D").then((m) => ({ default: m.Canvas3D })));

/**
 * The one renderer boundary (design §4): everything outside talks CanvasNode/CanvasEdge +
 * callbacks. Styles are resolved once here (styleEngine) and handed to the projection the
 * style config selects — Sigma (2D) or three.js (3D) — so path/subgraph overlays and all
 * "send to canvas" flows behave identically in both (studio-canvas-viz FR-9).
 */
export function GraphCanvas({
  nodes,
  edges,
  config,
  pathOverlay,
  emphasis,
  onSelect,
}: {
  nodes: Record<number, CanvasNode>;
  edges: Record<number, CanvasEdge>;
  config: StyleConfig;
  pathOverlay: PathREST | null;
  emphasis?: EmphasisSet | null;
  onSelect: (ref: ElementRef | null) => void;
}) {
  const overlay: PathOverlaySets = useMemo(() => {
    if (pathOverlay) {
      const nodeIds = new Set<number>();
      const edgeIds = new Set<number>();
      for (const el of pathOverlay.pathElements) {
        nodeIds.add(el.sourceVertexId);
        nodeIds.add(el.targetVertexId);
        edgeIds.add(el.edgeId);
      }
      return { nodeIds, edgeIds, active: true, dim: true };
    }
    if (emphasis) {
      return {
        nodeIds: new Set(emphasis.nodeIds),
        edgeIds: new Set(emphasis.edgeIds),
        active: true,
        dim: false,
      };
    }
    return EMPTY_OVERLAY;
  }, [pathOverlay, emphasis]);

  const styles = useMemo(
    () => resolveStyles(nodes, edges, overlay, config),
    [nodes, edges, overlay, config],
  );

  if (config.renderer === "3d") {
    return (
      <Suspense
        fallback={
          <div className="bg-ink text-fg-faint flex h-full w-full items-center justify-center text-[11px]">
            loading 3D renderer…
          </div>
        }
      >
        <Canvas3D nodes={nodes} edges={edges} styles={styles} config={config} onSelect={onSelect} />
      </Suspense>
    );
  }
  return <Canvas2D nodes={nodes} edges={edges} styles={styles} config={config} onSelect={onSelect} />;
}
