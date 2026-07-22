import { useState } from "react";
import { useQuery } from "@tanstack/react-query";
import { useActiveInstance } from "../instances/registry";
import {
  getInDegree,
  getInEdgeProperties,
  getInEdges,
  getOutDegree,
  getOutEdgeProperties,
  getOutEdges,
} from "../api/endpoints";
import type { VertexREST } from "../api/types";
import { InspectLink } from "./InspectLink";

export function AdjacencyPanel({ vertex }: { vertex: VertexREST }) {
  const instance = useActiveInstance()!;
  const [expanded, setExpanded] = useState<{ dir: "out" | "in"; prop: string } | null>(null);

  const outProps = useQuery({
    queryKey: [instance.id, "vertex", vertex.id, "edges-out"],
    queryFn: () => getOutEdgeProperties(instance, vertex.id),
  });
  const inProps = useQuery({
    queryKey: [instance.id, "vertex", vertex.id, "edges-in"],
    queryFn: () => getInEdgeProperties(instance, vertex.id),
  });
  const degrees = useQuery({
    queryKey: [instance.id, "vertex", vertex.id, "degrees"],
    queryFn: async () => {
      const [inDegree, outDegree] = await Promise.all([
        getInDegree(instance, vertex.id),
        getOutDegree(instance, vertex.id),
      ]);
      return {
        inDegree,
        outDegree,
        degree: (inDegree ?? 0) + (outDegree ?? 0),
      };
    },
  });
  const expandedEdges = useQuery({
    queryKey: [instance.id, "vertex", vertex.id, "edges", expanded?.dir, expanded?.prop],
    queryFn: () =>
      expanded!.dir === "out"
        ? getOutEdges(instance, vertex.id, expanded!.prop)
        : getInEdges(instance, vertex.id, expanded!.prop),
    enabled: expanded !== null,
  });

  return (
    <div className="panel">
      <div className="panel-title">adjacency</div>
      <div className="space-y-2 p-3 text-[12px]">
        <div className="text-fg-dim" data-testid="degrees">
          degree {degrees.data?.degree ?? "…"} · in {degrees.data?.inDegree ?? "…"} · out{" "}
          {degrees.data?.outDegree ?? "…"}
        </div>
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
                <InspectLink key={edgeId} id={edgeId} />
              ))}
              {expandedEdges.data?.length === 0 && (
                <span className="text-fg-faint">none</span>
              )}
            </div>
          </div>
        )}
      </div>
    </div>
  );
}
