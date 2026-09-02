// MIT License
//
// CanvasScreen.tsx
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

import { useMemo, useState } from "react";
import { useMutation, useQuery } from "@tanstack/react-query";
import { useInstanceStore } from "../instances/registry";
import { GraphCanvas, type ElementRef } from "../canvas/GraphCanvas";
import { StylePanel } from "../canvas/StylePanel";
import { FindPanel } from "../canvas/FindPanel";
import { ConnectPanel } from "../canvas/ConnectPanel";
import { InteractPanel } from "../canvas/InteractPanel";
import { buildLegend, knownPropertyKeys } from "../canvas/styleEngine";
import { GRADIENT_HIGH, GRADIENT_LOW } from "../canvas/styling";
import { getEdge, getGraph, getGraphElement, getStatus } from "../api/endpoints";
import { CANVAS_ELEMENT_CAP } from "../lib/canvasCap";
import { expandVertices } from "../lib/canvasInteract";
import { CANVAS_TABS } from "../state/instanceStore";
import { useNavigate } from "@tanstack/react-router";
import { useStatus } from "../state/status";
import { previewVector } from "../lib/embeddingProperties";
import { similarSearchesFor } from "../lib/findSimilar";
import { DISPLAY_CAP } from "../lib/truncate";
import { ErrorBox } from "../components/ErrorBox";
import { Truncated } from "../components/Truncated";

/**
 * Graph canvas screen (FR-18/19/20 + studio-canvas-viz + canvas-view-controls): renders the
 * active instance's canvas store in 2D or 3D, a sectioned style panel (data-driven
 * color/size/image/width, layouts, render toggles), a color legend, selection-driven detail
 * panel, remove-from-view (view only!), expand-on-demand which merges a vertex's edges +
 * neighbors, and the working-set controls: "Show whole graph" (an explicit, capped,
 * merge-only load; the canvas still never auto-loads anything) and "Clear view" (empties
 * the working set including the selection; style config and result sets survive).
 */
export function CanvasScreen() {
  const { instance, store } = useInstanceStore();
  // For the Detail panel's "find similar": the vector index bound to the element's embedding, and
  // the one-shot prefill that carries its own vector to the Query screen.
  const navigate = useNavigate();
  const setScanPrefill = store((state) => state.setScanPrefill);
  const inventory = useStatus(instance).data?.indices;
  const canvasNodes = store((s) => s.canvasNodes);
  const canvasEdges = store((s) => s.canvasEdges);
  const pathOverlay = store((s) => s.pathOverlay);
  const styleConfig = store((s) => s.styleConfig);
  const setStyleConfig = store((s) => s.setStyleConfig);
  const mergeIntoCanvas = store((s) => s.mergeIntoCanvas);
  const removeFromCanvas = store((s) => s.removeFromCanvas);
  const clearCanvas = store((s) => s.clearCanvas);
  const setPathOverlay = store((s) => s.setPathOverlay);
  const wholeGraphTruncation = store((s) => s.wholeGraphTruncation);
  const setWholeGraphTruncation = store((s) => s.setWholeGraphTruncation);
  const activeTab = store((s) => s.canvasToolsDraft.tab);
  const setCanvasToolsDraft = store((s) => s.setCanvasToolsDraft);

  const [selected, setSelected] = useState<ElementRef | null>(null);
  // Transient Find-tab hover spotlight (feature canvas-find-connect): the "eclipse" corona.
  const [hovered, setHovered] = useState<ElementRef | null>(null);

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
      // Expand-on-demand (FR-18) through the shared sweep (feature canvas-interact), which is
      // the ONE home for it: this is that sweep over a single id, so the focused-element gesture
      // and the Interact tab's bulk expand cannot drift apart. Merge-only, never a whole-graph
      // reload; endpoints already on the canvas are not re-fetched.
      await expandVertices(instance, [vertexId], {
        skipIds: () => new Set(Object.keys(store.getState().canvasNodes).map(Number)),
        onMerge: mergeIntoCanvas,
      });
    },
  });

  const wholeGraph = useMutation({
    mutationFn: async () => {
      // The merge happens only after BOTH fetches succeed, so a failed fetch leaves the
      // canvas untouched.
      const [graph, status] = await Promise.all([
        getGraph(instance, CANVAS_ELEMENT_CAP),
        getStatus(instance),
      ]);
      const g = graph ?? { vertices: [], edges: [] };
      mergeIntoCanvas(g.vertices, g.edges);
      const fetchedVertices = g.vertices.length;
      const fetchedEdges = g.edges.length;
      const totalVertices = status?.vertexCount ?? fetchedVertices;
      const totalEdges = status?.edgeCount ?? fetchedEdges;
      // The truncation record travels with the canvas it describes (persisted store state,
      // see WholeGraphTruncation), so the honest notice survives leaving and returning.
      // Fetched counts are the raw server payload, not the post-merge store, so stub nodes
      // synthesized by buildCanvasModel never inflate them.
      setWholeGraphTruncation(
        totalVertices > fetchedVertices || totalEdges > fetchedEdges
          ? { fetchedVertices, fetchedEdges, totalVertices, totalEdges }
          : null,
      );
    },
  });

  const truncationNotice = useMemo(() => {
    const t = wholeGraphTruncation;
    if (!t) return null;
    const parts: string[] = [];
    if (t.totalVertices > t.fetchedVertices) {
      parts.push(`${t.fetchedVertices.toLocaleString()} of ${t.totalVertices.toLocaleString()} vertices`);
    }
    if (t.totalEdges > t.fetchedEdges) {
      parts.push(`${t.fetchedEdges.toLocaleString()} of ${t.totalEdges.toLocaleString()} edges`);
    }
    return parts.length > 0 ? `showing the first ${parts.join(" and ")}` : null;
  }, [wholeGraphTruncation]);

  const elementCount = Object.keys(canvasNodes).length + Object.keys(canvasEdges).length;

  return (
    <div className="flex h-full gap-3">
      <div className="panel relative min-w-0 flex-1 overflow-hidden">
        <GraphCanvas
          nodes={canvasNodes}
          edges={canvasEdges}
          config={styleConfig}
          pathOverlay={pathOverlay}
          highlight={hovered}
          onSelect={setSelected}
        />
        <div className="absolute top-2 left-2 space-y-1">
          <div className="flex items-center gap-2">
            <span className="text-fg-dim text-[11px]">{elementCount} elements</span>
            {truncationNotice && (
              <span className="text-warn text-[11px]" data-testid="whole-graph-truncation">
                {truncationNotice}
              </span>
            )}
            {pathOverlay && (
              <button type="button" className="btn" onClick={() => setPathOverlay(null)}>
                Clear path overlay
              </button>
            )}
            <button
              type="button"
              className="btn"
              data-testid="show-whole-graph"
              disabled={wholeGraph.isPending}
              title={`Fetches up to ${CANVAS_ELEMENT_CAP.toLocaleString()} vertices and edges and merges them into this view. View only: the database is never touched.`}
              onClick={() => wholeGraph.mutate()}
            >
              {wholeGraph.isPending ? "Loading…" : "Show whole graph"}
            </button>
            <button
              type="button"
              className="btn btn-danger"
              disabled={elementCount === 0}
              onClick={() => {
                // Complete clear (canvas-view-controls FR-1): the selection is content too,
                // so the detail panel returns to its empty hint. clearCanvas also drops the
                // truncation record; the mutation reset dismisses a shown fetch error. A
                // clear during an in-flight load cancels nothing (FR-5): the late merge
                // lands together with its own truncation record.
                clearCanvas();
                setSelected(null);
                wholeGraph.reset();
              }}
            >
              Clear view
            </button>
          </div>
          {wholeGraph.isError && <ErrorBox error={wholeGraph.error} />}
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
                <Truncated
                  text={`${key} (${count})`}
                  max={DISPLAY_CAP.chipName}
                  className="text-fg-dim"
                />
              </div>
            ))
          )}
        </div>
      </div>

      <aside className="w-80 shrink-0 space-y-3 overflow-auto">
        <div className="panel">
          {/* Tool strip (feature canvas-find-connect, extended by canvas-interact): Style is the
              default; Find searches the graph, Connect links canvas vertices via path search, and
              Interact filters the working set to expand or remove it in bulk. The Detail panel
              below is selection-driven and independent of the active tab. */}
          <div className="border-line flex border-b" role="tablist">
            {CANVAS_TABS.map((t) => (
              <button
                key={t}
                type="button"
                role="tab"
                aria-selected={activeTab === t}
                data-testid={`canvas-tab-${t}`}
                className={`px-3 py-1.5 text-[11px] font-semibold tracking-wide uppercase ${
                  activeTab === t
                    ? "text-accent border-accent border-b-2"
                    : "text-fg-dim hover:text-fg"
                }`}
                onClick={() => setCanvasToolsDraft({ tab: t })}
              >
                {t}
              </button>
            ))}
          </div>
          {activeTab === "style" && (
            <StylePanel
              config={styleConfig}
              onChange={setStyleConfig}
              nodePropertyKeys={nodePropertyKeys}
              edgePropertyKeys={edgePropertyKeys}
            />
          )}
          {activeTab === "find" && <FindPanel onSelect={setSelected} onHover={setHovered} />}
          {activeTab === "connect" && <ConnectPanel />}
          {activeTab === "interact" && (
            <InteractPanel selected={selected} onSelect={setSelected} onHover={setHovered} />
          )}
        </div>
        <div className="panel">
          <div className="panel-title">detail</div>
          <div className="space-y-2 p-3 text-[12px]">
            {!selected && (
              <div className="text-fg-faint">
                Select a node or edge. Empty canvas? Send elements here from the browser,
                query, or traverse screens, or show the whole graph.
              </div>
            )}
            {selected && detail.isPending && <div className="text-fg-faint">loading…</div>}
            {selected && detail.isError && <ErrorBox error={detail.error} />}
            {selected && detail.data && (
              <>
                <div className="text-fg font-semibold">
                  {selected.kind} #{selected.id}
                </div>
                {selected.kind === "edge" && "edgePropertyId" in detail.data && (
                  <div className="flex gap-1">
                    <span className="text-fg-faint shrink-0">type </span>
                    <Truncated text={detail.data.edgePropertyId ?? "—"} className="min-w-0" />
                  </div>
                )}
                <div className="flex gap-1">
                  <span className="text-fg-faint shrink-0">label </span>
                  <Truncated text={detail.data.label ?? "—"} className="min-w-0" />
                </div>
                {/* table-fixed + per-cell truncate keeps every property on ONE line inside this
                    narrow (w-80) panel — a long value/URL/vector can't widen the table and spill
                    a horizontal scrollbar. The full value is in each cell's title tooltip. */}
                <table className="w-full table-fixed">
                  <tbody>
                    {(detail.data.properties ?? []).map((p) => {
                      const value = previewVector(p.propertyValue); // caps a vector to a short preview
                      return (
                        <tr key={p.propertyId}>
                          <td className="table-cell text-fg-faint w-2/5 truncate" title={p.propertyId}>
                            {p.propertyId}
                          </td>
                          <td className="table-cell truncate" title={value}>
                            {value}
                          </td>
                        </tr>
                      );
                    })}
                  </tbody>
                </table>
                <div className="flex flex-wrap gap-1 pt-1">
                  {(() => {
                    // The element's own vector IS the query. Offered only where a bound vector index
                    // projects the embedding, and constrained to the element's own label: several
                    // entity kinds embed as little more than their identifier, so an unconstrained
                    // similarity search ranks identifier-shaped noise against real matches.
                    const similar = similarSearchesFor(
                      detail.data!,
                      inventory,
                      selected.kind === "edge",
                    );
                    if (similar.length === 0) return null;
                    return (
                      <button
                        type="button"
                        className="btn"
                        data-testid="find-similar"
                        title={`search the '${similar[0].embeddingName}' vector index with this element's own vector`}
                        onClick={() => {
                          setScanPrefill(similar[0].prefill);
                          void navigate({ to: "/query" });
                        }}
                      >
                        Find similar
                      </button>
                    );
                  })()}
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
