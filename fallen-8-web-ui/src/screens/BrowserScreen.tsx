import { useState } from "react";
import { useMutation, useQuery } from "@tanstack/react-query";
import { useNavigate } from "@tanstack/react-router";
import { useActiveInstance } from "../instances/registry";
import {
  getEdge,
  getGraph,
  getGraphElement,
  getInDegree,
  getInEdgeProperties,
  getInEdges,
  getOutDegree,
  getOutEdgeProperties,
  getOutEdges,
  getVertex,
} from "../api/endpoints";
import type { EdgeREST, VertexREST } from "../api/types";
import { isEdge } from "../lib/hydrate";
import { isTruncated } from "../lib/truncation";
import { formatPropertyValue } from "../lib/literals";
import { ElementTable } from "../components/ElementTable";
import { ErrorBox } from "../components/ErrorBox";
import { Field } from "../components/Field";
import { help } from "../lib/fieldHelp";
import { MutationsPanel } from "../components/MutationsPanel";
import { getInstanceStore } from "../state/instanceStore";

/**
 * Element browser (FR-5/6/7): fetch a vertex/edge/graph element by id with typed
 * properties; one-click adjacency + degrees; bulk view with the mandatory truncation
 * indicator; "send to canvas".
 */

function AdjacencyPanel({ vertex }: { vertex: VertexREST }) {
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

function InspectLink({ id }: { id: number }) {
  const navigate = useNavigate();
  return (
    <button
      type="button"
      className="text-accent-2 cursor-pointer hover:underline"
      onClick={() => navigate({ to: "/browser", search: { id: String(id) } as never })}
    >
      #{id}
    </button>
  );
}

function ElementDetail({ element }: { element: VertexREST | EdgeREST }) {
  const edge = isEdge(element) ? element : null;
  return (
    <div className="panel">
      <div className="panel-title">
        {edge ? "edge" : "vertex"} #{element.id}
      </div>
      <div className="space-y-2 p-3 text-[12px]">
        <div>
          <span className="text-fg-faint">label </span>
          {element.label ?? "—"}
        </div>
        <div className="text-fg-dim">
          created {element.creationDate} · modified {element.modificationDate}
        </div>
        {edge && (
          <div>
            <span className="text-fg-faint">endpoints </span>
            <InspectLink id={edge.sourceVertex} /> → <InspectLink id={edge.targetVertex} />
          </div>
        )}
        <table className="w-full">
          <thead>
            <tr className="text-fg-faint">
              <th className="table-cell">property</th>
              <th className="table-cell">value</th>
              <th className="table-cell">type</th>
            </tr>
          </thead>
          <tbody>
            {(element.properties ?? []).map((p) => (
              <tr key={p.propertyId}>
                <td className="table-cell">{p.propertyId}</td>
                <td className="table-cell">{formatPropertyValue(p.propertyValue)}</td>
                <td className="table-cell text-fg-dim">{p.fullQualifiedTypeName ?? "—"}</td>
              </tr>
            ))}
            {(element.properties ?? []).length === 0 && (
              <tr>
                <td className="table-cell text-fg-faint" colSpan={3}>
                  no properties
                </td>
              </tr>
            )}
          </tbody>
        </table>
      </div>
    </div>
  );
}

export function BrowserScreen() {
  const instance = useActiveInstance()!;
  const store = getInstanceStore(instance.id);
  const mergeIntoCanvas = store((s) => s.mergeIntoCanvas);
  const [idInput, setIdInput] = useState("");
  const [lookupKind, setLookupKind] = useState<"graphelement" | "vertex" | "edge">(
    "graphelement",
  );
  const [maxElements, setMaxElements] = useState(1000);
  const [bulkFilter, setBulkFilter] = useState("");

  const lookup = useMutation({
    mutationFn: async ({
      kind,
      id,
    }: {
      kind: "graphelement" | "vertex" | "edge";
      id: number;
    }) => {
      if (!Number.isInteger(id)) throw new Error("Enter a numeric element id.");
      if (kind === "vertex") return await getVertex(instance, id);
      if (kind === "edge") return await getEdge(instance, id);
      return await getGraphElement(instance, id);
    },
  });

  const bulk = useQuery({
    queryKey: [instance.id, "graph", maxElements],
    queryFn: ({ signal }) => getGraph(instance, maxElements, signal),
    enabled: false,
  });

  const element = lookup.data ?? null;

  return (
    <div className="mx-auto max-w-5xl space-y-4">
      <section className="panel">
        <div className="panel-title">Element lookup</div>
        <form
          className="flex items-end gap-2 p-3"
          onSubmit={(e) => {
            e.preventDefault();
            lookup.mutate({ kind: lookupKind, id: Number(idInput) });
          }}
        >
          <Field helpKey="lookupKind" label="kind" htmlFor="lookup-kind">
            <select
              id="lookup-kind"
              className="input w-auto"
              value={lookupKind}
              onChange={(e) => setLookupKind(e.target.value as typeof lookupKind)}
            >
              <option value="graphelement">graph element</option>
              <option value="vertex">vertex</option>
              <option value="edge">edge</option>
            </select>
          </Field>
          <Field helpKey="lookupId" label="id" htmlFor="lookup-id">
            <input
              id="lookup-id"
              data-testid="lookup-id"
              className="input w-32"
              value={idInput}
              onChange={(e) => setIdInput(e.target.value)}
              placeholder="0"
            />
          </Field>
          <button type="submit" className="btn btn-accent" data-testid="lookup-go">
            Fetch
          </button>
          {element && !isEdge(element) && (
            <button
              type="button"
              className="btn ml-auto"
              onClick={() => mergeIntoCanvas([element], [])}
            >
              Send to canvas
            </button>
          )}
        </form>
        {lookup.isError && (
          <div className="px-3 pb-3">
            <ErrorBox error={lookup.error} />
          </div>
        )}
        {lookup.isSuccess && element === null && (
          <div className="text-fg-faint px-3 pb-3 text-[12px]">
            No element with that id (missing = empty, not an error).
          </div>
        )}
      </section>

      {element && (
        <div className="grid grid-cols-1 gap-4 lg:grid-cols-2">
          <ElementDetail element={element} />
          {!isEdge(element) && <AdjacencyPanel vertex={element} />}
        </div>
      )}

      <section className="panel">
        <div className="panel-title">Bulk graph</div>
        <div className="flex items-end gap-2 p-3">
          <Field helpKey="maxElements" label="maxElements" htmlFor="max-elements">
            <input
              id="max-elements"
              className="input w-28"
              type="number"
              min={1}
              value={maxElements}
              onChange={(e) => setMaxElements(Number(e.target.value) || 1000)}
            />
          </Field>
          <button type="button" className="btn" onClick={() => bulk.refetch()}>
            {bulk.isFetching ? "Loading…" : "Load"}
          </button>
          {bulk.data && (
            <>
              <span className="text-fg-dim text-[12px]">
                {bulk.data.vertices.length} vertices · {bulk.data.edges.length} edges
              </span>
              {isTruncated(bulk.data, maxElements) && (
                <span
                  data-testid="truncation-indicator"
                  className="border-warn/50 text-warn rounded border px-1.5 py-0.5 text-[10px] tracking-wider uppercase"
                >
                  truncated at {maxElements}
                </span>
              )}
              <button
                type="button"
                className="btn btn-accent ml-auto"
                onClick={() => mergeIntoCanvas(bulk.data!.vertices, bulk.data!.edges)}
              >
                Send to canvas
              </button>
            </>
          )}
        </div>
        {bulk.isError && (
          <div className="px-3 pb-3">
            <ErrorBox error={bulk.error} onRetry={() => bulk.refetch()} />
          </div>
        )}
        {bulk.data && (
          <>
            <div className="px-3 pb-2">
              <input
                aria-label="filter loaded elements"
                title={help("bulkFilter")}
                data-testid="bulk-filter"
                className="input w-72"
                value={bulkFilter}
                onChange={(e) => setBulkFilter(e.target.value)}
                placeholder="filter by label / id (first 200 shown)"
              />
            </div>
            <ElementTable
              elements={[...bulk.data.vertices, ...bulk.data.edges]
                .filter((element) => {
                  if (!bulkFilter.trim()) return true;
                  const needle = bulkFilter.trim().toLowerCase();
                  return (
                    String(element.id) === needle ||
                    (element.label ?? "").toLowerCase().includes(needle)
                  );
                })
                .slice(0, 200)}
              onInspect={(id) => {
                setIdInput(String(id));
                setLookupKind("graphelement");
                lookup.mutate({ kind: "graphelement", id });
              }}
            />
          </>
        )}
      </section>

      <MutationsPanel />
    </div>
  );
}
