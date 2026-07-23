import { useMemo, useState } from "react";
import { useMutation, useQuery } from "@tanstack/react-query";
import { useInstanceStore } from "../instances/registry";
import { GraphCanvas, type ElementRef } from "../canvas/GraphCanvas";
import { StylePanel } from "../canvas/StylePanel";
import { buildLegend, knownPropertyKeys } from "../canvas/styleEngine";
import { GRADIENT_HIGH, GRADIENT_LOW } from "../canvas/styling";
import { getEdge, getGraphElement } from "../api/endpoints";
import { EXPAND_EDGE_CAP, fetchVertexNeighborhood } from "../lib/neighborhood";
import { formatPropertyValue } from "../lib/literals";
import { ErrorBox } from "../components/ErrorBox";

/**
 * Graph canvas screen (FR-18/19/20 + studio-canvas-viz): renders the active instance's
 * canvas store in 2D or 3D, a sectioned style panel (data-driven color/size/image/width,
 * layouts, render toggles), a color legend, selection-driven detail panel, remove-from-view
 * (view only!), and expand-on-demand which merges a vertex's edges + neighbors.
 */
export function CanvasScreen() {
  const { instance, store } = useInstanceStore();
  const canvasNodes = store((s) => s.canvasNodes);
  const canvasEdges = store((s) => s.canvasEdges);
  const pathOverlay = store((s) => s.pathOverlay);
  const styleConfig = store((s) => s.styleConfig);
  const setStyleConfig = store((s) => s.setStyleConfig);
  const mergeIntoCanvas = store((s) => s.mergeIntoCanvas);
  const removeFromCanvas = store((s) => s.removeFromCanvas);
  const clearCanvas = store((s) => s.clearCanvas);
  const setPathOverlay = store((s) => s.setPathOverlay);

  const [selected, setSelected] = useState<ElementRef | null>(null);

  const legend = useMemo(
    () => buildLegend(canvasNodes, styleConfig),
    [canvasNodes, styleConfig],
  );
  const nodePropertyKeys = useMemo(
    () => knownPropertyKeys(Object.values(canvasNodes)),
    [canvasNodes],
  );
  const edgePropertyKeys = useMemo(
    () => knownPropertyKeys(Object.values(canvasEdges)),
    [canvasEdges],
  );

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
      // Expand-on-demand (FR-18): hydrate the vertex's 1-hop neighborhood and merge -
      // never a whole-graph reload. Endpoints already on the canvas are not re-fetched.
      const { vertices, edges } = await fetchVertexNeighborhood(instance, vertexId, {
        cap: EXPAND_EDGE_CAP,
        skipNeighborIds: new Set(Object.keys(canvasNodes).map(Number)),
      });
      mergeIntoCanvas(vertices, edges);
    },
  });

  const elementCount = Object.keys(canvasNodes).length + Object.keys(canvasEdges).length;

  return (
    <div className="flex h-full gap-3">
      <div className="panel relative min-w-0 flex-1 overflow-hidden">
        <GraphCanvas
          nodes={canvasNodes}
          edges={canvasEdges}
          config={styleConfig}
          pathOverlay={pathOverlay}
          onSelect={setSelected}
        />
        <div className="absolute top-2 left-2 flex items-center gap-2">
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
          {legend.kind === "gradient" ? (
            <div className="text-[11px]">
              <div className="text-fg-dim">{legend.title}</div>
              <div className="flex items-center gap-1.5">
                <span className="text-fg-dim">{legend.min}</span>
                <span
                  className="inline-block h-2 w-24 rounded"
                  style={{
                    background: `linear-gradient(to right, ${GRADIENT_LOW}, ${GRADIENT_HIGH})`,
                  }}
                />
                <span className="text-fg-dim">{legend.max}</span>
              </div>
            </div>
          ) : (
            legend.entries.map(({ key, color, count }) => (
              <div key={key} className="flex items-center gap-1.5 text-[11px]">
                <span
                  className="inline-block h-2.5 w-2.5 rounded-full"
                  style={{ backgroundColor: color }}
                />
                <span className="text-fg-dim">
                  {key} ({count})
                </span>
              </div>
            ))
          )}
        </div>
      </div>

      <aside className="w-80 shrink-0 space-y-3 overflow-auto">
        <div className="panel">
          <div className="panel-title">style</div>
          <StylePanel
            config={styleConfig}
            onChange={setStyleConfig}
            nodePropertyKeys={nodePropertyKeys}
            edgePropertyKeys={edgePropertyKeys}
          />
        </div>
        <div className="panel">
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
        </div>
      </aside>
    </div>
  );
}
