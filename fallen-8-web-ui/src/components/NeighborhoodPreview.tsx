import { useMemo, useRef } from "react";
import { keepPreviousData, useQuery } from "@tanstack/react-query";
import { useInstanceStore } from "../instances/registry";
import type { EdgeREST, VertexREST } from "../api/types";
import { isEdge } from "../lib/hydrate";
import {
  fetchEdgeNeighborhood,
  fetchVertexNeighborhood,
  PREVIEW_EDGE_CAP,
} from "../lib/neighborhood";
import { buildCanvasModel, type CanvasModel } from "../state/instanceStore";
import { GraphCanvas } from "../canvas/GraphCanvas";
import { ErrorBox } from "./ErrorBox";
import { TruncationBadge } from "./TruncationBadge";

/**
 * Rendered 1-hop neighborhood (feature adjacency-preview): a vertex with its adjacent
 * edges + neighbors, or an edge between its endpoints with every parallel edge — the
 * focus element emphasized, clicks navigating via onInspect.
 */
export function NeighborhoodPreview({
  element,
  onInspect,
}: {
  element: VertexREST | EdgeREST;
  onInspect: (id: number) => void;
}) {
  const { instance, store } = useInstanceStore();
  const edge = isEdge(element) ? element : null;

  // Mirror the canvas: adopt the instance's persisted styling (node color/size and the
  // image/emoji property, so icons render here exactly as on the Canvas screen) but pin
  // to the 2D renderer — the preview is a teaser and must not lazy-load three.js.
  const styleConfig = store((s) => s.styleConfig);
  const previewStyle = useMemo(
    () => ({ ...styleConfig, renderer: "2d" as const }),
    [styleConfig],
  );

  const neighborhood = useQuery({
    queryKey: [instance.id, "neighborhood", edge ? "edge" : "vertex", element.id],
    queryFn: () =>
      edge
        ? fetchEdgeNeighborhood(instance, edge, { cap: PREVIEW_EDGE_CAP })
        : fetchVertexNeighborhood(instance, element.id, {
            cap: PREVIEW_EDGE_CAP,
            // The focus vertex is already hydrated by the lookup - seed, don't re-fetch.
            skipNeighborIds: new Set([element.id]),
          }),
    // A hop keeps the previous graph mounted and morphs it when the new neighborhood
    // lands - remount-and-spinner would flicker on every traversal step.
    placeholderData: keepPreviousData,
  });

  const lastFreshModel = useRef<CanvasModel | null>(null);
  const model = useMemo(() => {
    if (!neighborhood.data) return null;
    const vertices = edge
      ? neighborhood.data.vertices
      : [element as VertexREST, ...neighborhood.data.vertices];
    if (neighborhood.isPlaceholderData) {
      // Placeholder frame of a hop: the OLD focus vertex is not in the stale vertices
      // list (it was seeded at fetch time) - keep the last fresh model underneath so it
      // holds its label instead of demoting to a stub until the new neighborhood lands.
      return buildCanvasModel(vertices, neighborhood.data.edges, lastFreshModel.current ?? undefined);
    }
    const fresh = buildCanvasModel(vertices, neighborhood.data.edges);
    lastFreshModel.current = fresh;
    return fresh;
  }, [neighborhood.data, neighborhood.isPlaceholderData, element, edge]);

  const emphasis = useMemo(
    () =>
      edge
        ? { nodeIds: [] as number[], edgeIds: [element.id] }
        : { nodeIds: [element.id], edgeIds: [] as number[] },
    [edge, element.id],
  );

  if (neighborhood.isPending) {
    return <div className="text-fg-faint p-3 text-[12px]">loading neighborhood…</div>;
  }
  if (neighborhood.isError) {
    return (
      <div className="p-3">
        <ErrorBox error={neighborhood.error} onRetry={() => neighborhood.refetch()} />
      </div>
    );
  }

  const { edges, truncated } = neighborhood.data;
  return (
    <div className="space-y-2 p-3">
      <div className="flex items-center gap-2 text-[12px]">
        <span className="text-fg-dim" data-testid="preview-caption">
          {neighborhood.isPlaceholderData
            ? "…"
            : edge
              ? `${edges.length} edge${edges.length === 1 ? "" : "s"} between #${edge.sourceVertex} and #${edge.targetVertex}`
              : `${edges.length} edge${edges.length === 1 ? "" : "s"} · click an element to inspect it`}
        </span>
        {!neighborhood.isPlaceholderData && truncated && (
          <TruncationBadge testId="preview-truncation">
            capped at {PREVIEW_EDGE_CAP}
          </TruncationBadge>
        )}
      </div>
      <div
        className="border-line h-72 overflow-hidden rounded border"
        data-testid="neighborhood-preview"
      >
        <GraphCanvas
          nodes={model!.nodes}
          edges={model!.edges}
          config={previewStyle}
          pathOverlay={null}
          emphasis={emphasis}
          onSelect={(ref) => {
            if (ref) onInspect(ref.id);
          }}
        />
      </div>
    </div>
  );
}
