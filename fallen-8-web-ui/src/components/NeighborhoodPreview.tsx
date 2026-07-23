import { useMemo } from "react";
import { keepPreviousData, useQuery } from "@tanstack/react-query";
import { useActiveInstance } from "../instances/registry";
import type { EdgeREST, VertexREST } from "../api/types";
import { isEdge } from "../lib/hydrate";
import {
  fetchEdgeNeighborhood,
  fetchVertexNeighborhood,
  PREVIEW_EDGE_CAP,
} from "../lib/neighborhood";
import { buildCanvasModel } from "../state/instanceStore";
import { GraphCanvas } from "../canvas/GraphCanvas";
import { DEFAULT_STYLE_CONFIG, type StyleConfig } from "../canvas/styleConfig";
import { ErrorBox } from "./ErrorBox";

/** Fixed render config — the preview is a canvas teaser, not a workbench (spec non-goals). */
const PREVIEW_STYLE: StyleConfig = { ...DEFAULT_STYLE_CONFIG, edgeArrows: true };

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
  const instance = useActiveInstance()!;
  const edge = isEdge(element) ? element : null;

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

  const model = useMemo(() => {
    if (!neighborhood.data) return null;
    const vertices = edge
      ? neighborhood.data.vertices
      : [element as VertexREST, ...neighborhood.data.vertices];
    return buildCanvasModel(vertices, neighborhood.data.edges);
  }, [neighborhood.data, element, edge]);

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
          <span
            data-testid="preview-truncation"
            className="border-warn/50 text-warn rounded border px-1.5 py-0.5 text-[10px] tracking-wider uppercase"
          >
            first {PREVIEW_EDGE_CAP} edges
          </span>
        )}
      </div>
      <div
        className="border-line h-72 overflow-hidden rounded border"
        data-testid="neighborhood-preview"
      >
        <GraphCanvas
          nodes={model!.nodes}
          edges={model!.edges}
          config={PREVIEW_STYLE}
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
