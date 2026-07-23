import { useState } from "react";
import { useMutation, useQuery } from "@tanstack/react-query";
import { useInstanceStore } from "../instances/registry";
import { getEdge, getGraph, getGraphElement, getVertex } from "../api/endpoints";
import { isEdge } from "../lib/hydrate";
import { isTruncated } from "../lib/truncation";
import { AdjacencyPanel } from "../components/AdjacencyPanel";
import { ElementDetail } from "../components/ElementDetail";
import { ElementTable } from "../components/ElementTable";
import { ErrorBox } from "../components/ErrorBox";
import { Field } from "../components/Field";
import { help } from "../lib/fieldHelp";
import { MutationsPanel } from "../components/MutationsPanel";
import { NeighborhoodPreview } from "../components/NeighborhoodPreview";
import { useEmbeddingProvider } from "../state/graphShape";

/**
 * Element browser (FR-5/6/7): fetch a vertex/edge/graph element by id with typed
 * properties; one-click adjacency + degrees; bulk view with the mandatory truncation
 * indicator; "send to canvas".
 */
export function BrowserScreen() {
  const { instance, store } = useInstanceStore();
  const mergeIntoCanvas = store((s) => s.mergeIntoCanvas);
  const [idInput, setIdInput] = useState("");
  const [lookupKind, setLookupKind] = useState<"graphelement" | "vertex" | "edge">(
    "graphelement",
  );
  const [maxElements, setMaxElements] = useState(1000);
  const [bulkFilter, setBulkFilter] = useState("");
  const [detailTab, setDetailTab] = useState<"properties" | "embeddings">("properties");

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

  const provider = useEmbeddingProvider(instance);
  const providerEnabled = provider ? provider.enabled : null;

  const element = lookup.data ?? null;

  // The one navigation mechanism on this screen: endpoint links, edge-id chips, the
  // bulk table, and the neighborhood preview all land here (feature adjacency-preview).
  const inspect = (id: number) => {
    setIdInput(String(id));
    setLookupKind("graphelement");
    lookup.mutate({ kind: "graphelement", id });
  };

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
          <Field helpKey="elementId" label="id" htmlFor="lookup-id">
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
          <ElementDetail
            element={element}
            providerEnabled={providerEnabled}
            onRefresh={() => lookup.mutate({ kind: lookupKind, id: element.id })}
            onInspect={inspect}
            tab={detailTab}
            onTabChange={setDetailTab}
          />
          {isEdge(element) ? (
            <section className="panel">
              <div className="panel-title">neighborhood</div>
              <NeighborhoodPreview element={element} onInspect={inspect} />
            </section>
          ) : (
            <AdjacencyPanel vertex={element} onInspect={inspect} />
          )}
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
              onInspect={inspect}
            />
          </>
        )}
      </section>

      <MutationsPanel />
    </div>
  );
}
