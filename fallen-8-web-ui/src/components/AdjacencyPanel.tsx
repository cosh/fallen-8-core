import { useState } from "react";
import { useQuery } from "@tanstack/react-query";
import { useInstanceStore } from "../instances/registry";
import {
  getInDegree,
  getInEdgeProperties,
  getInEdges,
  getOutDegree,
  getOutEdgeProperties,
  getOutEdges,
} from "../api/endpoints";
import type { EdgeREST, VertexREST } from "../api/types";
import { isEdge } from "../lib/hydrate";
import { InspectLink } from "./InspectLink";
import { NeighborhoodPreview } from "./NeighborhoodPreview";

/** graph (default): rendered 1-hop preview; stats: degrees + per-property edge listing. */
type AdjacencyView = "graph" | "stats";

/**
 * Right-column adjacency panel (feature adjacency-preview). A vertex offers graph|stats;
 * an edge always renders the neighborhood preview (endpoints + parallel bundle). One
 * component for both kinds so the preview instance survives vertex↔edge hops instead of
 * remounting (which would flicker).
 */
export function AdjacencyPanel({
  element,
  onInspect,
}: {
  element: VertexREST | EdgeREST;
  onInspect: (id: number) => void;
}) {
  const { instance } = useInstanceStore();
  const [view, setView] = useState<AdjacencyView>("graph");
  const [expanded, setExpanded] = useState<{ dir: "out" | "in"; prop: string } | null>(null);
  const edge = isEdge(element);
  const statsEnabled = !edge && view === "stats";

  const outProps = useQuery({
    queryKey: [instance.id, "vertex", element.id, "edges-out"],
    queryFn: () => getOutEdgeProperties(instance, element.id),
    enabled: statsEnabled,
  });
  const inProps = useQuery({
    queryKey: [instance.id, "vertex", element.id, "edges-in"],
    queryFn: () => getInEdgeProperties(instance, element.id),
    enabled: statsEnabled,
  });
  const degrees = useQuery({
    queryKey: [instance.id, "vertex", element.id, "degrees"],
    queryFn: async () => {
      const [inDegree, outDegree] = await Promise.all([
        getInDegree(instance, element.id),
        getOutDegree(instance, element.id),
      ]);
      return {
        inDegree,
        outDegree,
        degree: (inDegree ?? 0) + (outDegree ?? 0),
      };
    },
    enabled: !edge,
  });
  const expandedEdges = useQuery({
    queryKey: [instance.id, "vertex", element.id, "edges", expanded?.dir, expanded?.prop],
    queryFn: () =>
      expanded!.dir === "out"
        ? getOutEdges(instance, element.id, expanded!.prop)
        : getInEdges(instance, element.id, expanded!.prop),
    enabled: statsEnabled && expanded !== null,
  });

  return (
    <div className="panel">
      <div className="panel-title">
        <span>{edge ? "neighborhood" : "adjacency"}</span>
        {!edge && (
          <div className="ml-auto flex gap-1 normal-case">
            {(["graph", "stats"] as const).map((v) => (
              <button
                key={v}
                type="button"
                data-testid={`adjacency-view-${v}`}
                className={`cursor-pointer px-1.5 py-0.5 text-[10px] tracking-wider uppercase ${
                  view === v ? "text-accent" : "text-fg-dim hover:text-fg"
                }`}
                onClick={() => setView(v)}
              >
                {v}
              </button>
            ))}
          </div>
        )}
      </div>
      {!edge && (
        <div className="text-fg-dim p-3 pb-0 text-[12px]" data-testid="degrees">
          degree {degrees.data?.degree ?? "…"} · in {degrees.data?.inDegree ?? "…"} · out{" "}
          {degrees.data?.outDegree ?? "…"}
        </div>
      )}
      {edge || view === "graph" ? (
        <NeighborhoodPreview element={element} onInspect={onInspect} />
      ) : (
        <div className="space-y-2 p-3 text-[12px]">
          {(["out", "in"] as const).map((dir) => {
            const props = dir === "out" ? outProps : inProps;
            return (
              <div key={dir}>
                <div className="text-fg-faint text-[10px] tracking-widest uppercase">
                  {dir === "out" ? "outgoing" : "incoming"}
                </div>
                {props.data === null || props.data?.length === 0 ? (
                  <div className="text-fg-faint">none</div>
                ) : (
                  (props.data ?? []).map((prop) => (
                    <button
                      key={prop}
                      type="button"
                      className="btn mt-1 mr-1"
                      onClick={() => setExpanded({ dir, prop })}
                    >
                      {prop}
                    </button>
                  ))
                )}
              </div>
            );
          })}
          {expanded && (
            <div>
              <div className="text-fg-faint text-[10px] tracking-widest uppercase">
                {expanded.dir} · {expanded.prop} edge ids
              </div>
              <div className="mt-1 flex flex-wrap gap-1">
                {(expandedEdges.data ?? []).map((edgeId) => (
                  <InspectLink key={edgeId} id={edgeId} onInspect={onInspect} />
                ))}
                {expandedEdges.data?.length === 0 && (
                  <span className="text-fg-faint">none</span>
                )}
              </div>
            </div>
          )}
        </div>
      )}
    </div>
  );
}
