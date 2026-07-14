import { useMemo, useState } from "react";
import { useMutation, useQuery } from "@tanstack/react-query";
import { useActiveInstance } from "../instances/registry";
import { getInstanceStore } from "../state/instanceStore";
import { GraphCanvas, type ElementRef } from "../canvas/GraphCanvas";
import { colorForLabel } from "../canvas/styling";
import {
  getEdge,
  getGraphElement,
  getInEdgeProperties,
  getInEdges,
  getOutEdgeProperties,
  getOutEdges,
} from "../api/endpoints";
import type { EdgeREST, VertexREST } from "../api/types";
import { formatPropertyValue } from "../lib/literals";
import { ErrorBox } from "../components/ErrorBox";

/**
 * Graph canvas screen (FR-18/19/20): renders the active instance's canvas store, offers
 * force/circular layouts, a label legend, selection-driven detail panel, remove-from-view
 * (view only!), and expand-on-demand which merges a vertex's edges + neighbors.
 */
export function CanvasScreen() {
  const instance = useActiveInstance()!;
  const store = getInstanceStore(instance.id);
  const canvasNodes = store((s) => s.canvasNodes);
  const canvasEdges = store((s) => s.canvasEdges);
  const pathOverlay = store((s) => s.pathOverlay);
  const mergeIntoCanvas = store((s) => s.mergeIntoCanvas);
  const removeFromCanvas = store((s) => s.removeFromCanvas);
  const clearCanvas = store((s) => s.clearCanvas);
  const setPathOverlay = store((s) => s.setPathOverlay);

  const [layout, setLayout] = useState<"force" | "circular">("force");
  const [selected, setSelected] = useState<ElementRef | null>(null);

  const legend = useMemo(() => {
    const labels = new Map<string, number>();
    for (const node of Object.values(canvasNodes)) {
      const label = node.label ?? "(unlabeled)";
      labels.set(label, (labels.get(label) ?? 0) + 1);
    }
    return [...labels.entries()].sort((a, b) => b[1] - a[1]).slice(0, 12);
  }, [canvasNodes]);

  const detail = useQuery({
    queryKey: [instance.id, "element", selected?.kind, selected?.id],
    queryFn: () =>
      selected!.kind === "edge"
        ? getEdge(instance, selected!.id)
        : getGraphElement(instance, selected!.id),
    enabled: selected !== null,
  });

  const expand = useMutation({
    mutationFn: async (vertexId: number) => {
      // Expand-on-demand (FR-18): fetch the vertex's edge ids per property (FR-6),
      // hydrate the edges, and merge - never a whole-graph reload.
      const [outProps, inProps] = await Promise.all([
        getOutEdgeProperties(instance, vertexId).catch(() => []),
        getInEdgeProperties(instance, vertexId).catch(() => []),
      ]);
      const edgeIdLists = await Promise.all([
        ...(outProps ?? []).map((p) => getOutEdges(instance, vertexId, p).catch(() => [])),
        ...(inProps ?? []).map((p) => getInEdges(instance, vertexId, p).catch(() => [])),
      ]);
      const edgeIds = [...new Set(edgeIdLists.flatMap((ids) => ids ?? []))].slice(0, 200);
      const edges = (
        await Promise.all(edgeIds.map((id) => getEdge(instance, id).catch(() => null)))
      ).filter((e): e is EdgeREST => e !== null);

      const neighborIds = new Set<number>();
      for (const edge of edges) {
        neighborIds.add(edge.sourceVertex);
        neighborIds.add(edge.targetVertex);
      }
      const neighbors = (
        await Promise.all(
          [...neighborIds]
            .filter((id) => !canvasNodes[id])
            .slice(0, 200)
            .map((id) => getGraphElement(instance, id).catch(() => null)),
        )
      ).filter((v): v is VertexREST => v !== null);

      mergeIntoCanvas(neighbors, edges);
    },
  });

  const elementCount = Object.keys(canvasNodes).length + Object.keys(canvasEdges).length;

  return (
    <div className="flex h-full gap-3">
      <div className="panel relative min-w-0 flex-1 overflow-hidden">
        <GraphCanvas
          nodes={canvasNodes}
          edges={canvasEdges}
          layout={layout}
          pathOverlay={pathOverlay}
          onSelect={setSelected}
        />
        <div className="absolute top-2 left-2 flex items-center gap-2">
          <select
            aria-label="layout"
            className="input w-auto"
            value={layout}
            onChange={(e) => setLayout(e.target.value as typeof layout)}
          >
            <option value="force">force (FA2)</option>
            <option value="circular">circular</option>
          </select>
          <span className="text-fg-dim text-[11px]">{elementCount} elements</span>
          {pathOverlay && (
            <button type="button" className="btn" onClick={() => setPathOverlay(null)}>
              Clear path overlay
            </button>
          )}
          <button
            type="button"
            className="btn btn-danger"
            disabled={elementCount === 0}
            onClick={() => clearCanvas()}
          >
            Clear view
          </button>
        </div>
        <div className="absolute bottom-2 left-2 space-y-0.5">
          {legend.map(([label, count]) => (
            <div key={label} className="flex items-center gap-1.5 text-[11px]">
              <span
                className="inline-block h-2.5 w-2.5 rounded-full"
                style={{
                  backgroundColor: colorForLabel(label === "(unlabeled)" ? null : label),
                }}
              />
              <span className="text-fg-dim">
                {label} ({count})
              </span>
            </div>
          ))}
        </div>
      </div>

      <aside className="panel w-80 shrink-0 overflow-auto">
        <div className="panel-title">detail</div>
        <div className="space-y-2 p-3 text-[12px]">
          {!selected && (
            <div className="text-fg-faint">
              Select a node or edge. Empty canvas? Send elements here from the browser,
              query, path, or subgraph screens.
            </div>
          )}
          {selected && detail.isPending && <div className="text-fg-faint">loading…</div>}
          {selected && detail.isError && <ErrorBox error={detail.error} />}
          {selected && detail.data && (
            <>
              <div className="text-fg font-semibold">
                {selected.kind} #{selected.id}
              </div>
              <div>
                <span className="text-fg-faint">label </span>
                {detail.data.label ?? "—"}
              </div>
              <table className="w-full">
                <tbody>
                  {(detail.data.properties ?? []).map((p) => (
                    <tr key={p.propertyId}>
                      <td className="table-cell text-fg-faint">{p.propertyId}</td>
                      <td className="table-cell">{formatPropertyValue(p.propertyValue)}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
              <div className="flex flex-wrap gap-1 pt-1">
                {selected.kind === "node" && (
                  <button
                    type="button"
                    className="btn btn-accent"
                    data-testid="expand-node"
                    disabled={expand.isPending}
                    onClick={() => expand.mutate(selected.id)}
                  >
                    {expand.isPending ? "Expanding…" : "Expand neighbors"}
                  </button>
                )}
                <button
                  type="button"
                  className="btn"
                  onClick={() => {
                    removeFromCanvas(selected.kind, selected.id);
                    setSelected(null);
                  }}
                >
                  Remove from view
                </button>
              </div>
              <p className="text-fg-faint text-[10px]">
                “Remove from view” only affects this canvas — it never deletes from the
                database.
              </p>
              {expand.isError && <ErrorBox error={expand.error} />}
            </>
          )}
        </div>
      </aside>
    </div>
  );
}
