// MIT License
//
// GraphCanvas.tsx
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
  highlight,
  onSelect,
}: {
  nodes: Record<number, CanvasNode>;
  edges: Record<number, CanvasEdge>;
  config: StyleConfig;
  pathOverlay: PathREST | null;
  emphasis?: EmphasisSet | null;
  /** Transient hover spotlight (feature canvas-find-connect): the "eclipse" corona is drawn over
   *  this node while a Find result row is hovered. Only a node kind spotlights; edges are ignored. */
  highlight?: ElementRef | null;
  onSelect: (ref: ElementRef | null) => void;
}) {
  const highlightId = highlight && highlight.kind === "node" ? highlight.id : null;
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
        <Canvas3D
          nodes={nodes}
          edges={edges}
          styles={styles}
          config={config}
          highlightId={highlightId}
          onSelect={onSelect}
        />
      </Suspense>
    );
  }
  return (
    <Canvas2D
      nodes={nodes}
      edges={edges}
      styles={styles}
      config={config}
      highlightId={highlightId}
      onSelect={onSelect}
    />
  );
}
