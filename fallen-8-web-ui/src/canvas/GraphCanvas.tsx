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

import { lazy, Suspense, useMemo, type Ref } from "react";
import type { CanvasEdge, CanvasNode } from "../state/instanceStore";
import type { PathREST } from "../api/types";
import type { StyleConfig } from "./styleConfig";
import { EMPTY_OVERLAY, resolveStyles, type PathOverlaySets } from "./styleEngine";
import { Canvas2D } from "./Canvas2D";

export type ElementRef = { kind: "node" | "edge"; id: number };

/**
 * Imperative camera control on a mounted canvas (feature canvas-host-controls). A host embedding
 * the canvas has no other way to reach the camera, so this is the whole surface: frame everything,
 * read the zoom, set the zoom.
 *
 * `ratio` is sigma's camera ratio in BOTH renderers: 1 means the graph just fits the padded
 * viewport and 2 is twice as far out. It is DERIVED on every call rather than stored, so 1 still
 * means "fits" after the graph grows, the container changes, or the renderer remounts.
 *
 * Multiplying it by k spreads the layout over 1/k of the distance but shrinks each element to
 * 1/sqrt(k) of its size, because sigma scales lengths by `sqrt(ratio)` (`scaleSize`) while it scales
 * positions by the ratio itself. So zooming out is not a uniform shrink, and a host computing sizes
 * needs the square root. Labels do not scale at all.
 *
 * Two renderer differences worth knowing. `setCameraRatio` keeps the pan in 2D but re-aims at the
 * graph origin in 3D (which is what a fit does there). And only 3D bounds the number: its orbit
 * controls cap the camera distance at the sky radius, whereas 2D applies any finite positive ratio
 * verbatim, because sigma's `minCameraRatio`/`maxCameraRatio` are left unset. A host driving the
 * ratio from a slider therefore owns the range check in 2D; `fitToView()` is always the way back.
 *
 * `paddingPx` is CSS px of inset around the graph, and omitting it asks for the renderer's own
 * framing rather than a shared number: 2D uses sigma's live `stagePadding`, 3D uses
 * `FIT_PADDING_PX`. The two are not comparable (3D padding shrinks the field of view against
 * container HEIGHT only), so copying one renderer's digit into the other would be false precision.
 * The arithmetic for both lives in canvas/eclipse.ts.
 */
export interface F8GraphCanvasHandle {
  fitToView(durationMs?: number, paddingPx?: number): void;
  getCameraRatio(): number;
  setCameraRatio(ratio: number): void;
}

/** Fit tween length in ms. Canvas3D has framed its mount auto-fit at 600 since day one. */
export const FIT_DURATION_MS = 600;

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
  ref,
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
  /** Camera handle, forwarded to whichever renderer is mounted (React 19 passes ref as a prop). */
  ref?: Ref<F8GraphCanvasHandle>;
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
          ref={ref}
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
      ref={ref}
    />
  );
}
