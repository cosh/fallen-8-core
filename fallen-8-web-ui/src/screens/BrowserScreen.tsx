import { useState } from "react";
import { useMutation, useQuery } from "@tanstack/react-query";
import { useNavigate } from "@tanstack/react-router";
import { useActiveInstance } from "../instances/registry";
import {
  deleteElementEmbedding,
  embedElement,
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
  putElementEmbedding,
} from "../api/endpoints";
import type { EdgeREST, PropertyREST, VertexREST } from "../api/types";
import { isEdge } from "../lib/hydrate";
import { isTruncated } from "../lib/truncation";
import { formatPropertyValue } from "../lib/literals";
import { parseVector } from "../lib/vector";
import { ElementTable } from "../components/ElementTable";
import { ErrorBox } from "../components/ErrorBox";
import { Field } from "../components/Field";
import { help } from "../lib/fieldHelp";
import { MutationsPanel } from "../components/MutationsPanel";
import { getInstanceStore } from "../state/instanceStore";
import {
  embeddingProvider,
  useGraphShape,
  EMBEDDING_PROPERTY_PREFIX as EMBEDDING_PREFIX,
  EMBEDDING_MODEL_PROPERTY_PREFIX as EMBEDDING_MODEL_PREFIX,
} from "../state/graphShape";
import type { InstanceConfig } from "../instances/types";

/** A property is reserved (embedding state) when it uses either embedding prefix. */
function isReservedEmbeddingProperty(propertyId: string): boolean {
  return (
    propertyId.startsWith(EMBEDDING_PREFIX) || propertyId.startsWith(EMBEDDING_MODEL_PREFIX)
  );
}

/** A one-line preview of a stored vector value (arrays would otherwise dump huge). */
function previewVector(value: unknown): string {
  if (Array.isArray(value)) {
    const head = value
      .slice(0, 4)
      .map((n) => (typeof n === "number" ? Number(n.toFixed(4)) : n))
      .join(", ");
    return `[${head}${value.length > 4 ? ", …" : ""}] (d=${value.length})`;
  }
  return formatPropertyValue(value);
}

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

/**
 * Named-embedding management for one element (feature element-embeddings): lists the
 * element's embeddings (folded out of the plain property table), and sets/replaces/removes
 * one via the typed endpoints. Bring-your-own-vector (paste) is always available; text-in
 * needs the embedding provider. onRefresh re-fetches the element so the list reflects the
 * write. Server 400/404/409 reasons render verbatim.
 */
function EmbeddingsTab({
  instance,
  element,
  providerEnabled,
  onRefresh,
}: {
  instance: InstanceConfig;
  element: VertexREST | EdgeREST;
  providerEnabled: boolean | null;
  onRefresh: () => void;
}) {
  const properties = element.properties ?? [];
  const embeddings = properties
    .filter((p) => p.propertyId.startsWith(EMBEDDING_PREFIX))
    .map((p) => {
      const name = p.propertyId.slice(EMBEDDING_PREFIX.length);
      const stamp = properties.find((s) => s.propertyId === EMBEDDING_MODEL_PREFIX + name);
      return { name, value: p.propertyValue, model: stamp?.propertyValue ?? null };
    });

  const [name, setName] = useState("default");
  const [source, setSource] = useState<"vector" | "text">("vector");
  const [vectorText, setVectorText] = useState("");
  const [text, setText] = useState("");
  const textUnavailable = providerEnabled !== true;

  const write = useMutation({
    mutationFn: async () => {
      const trimmedName = name.trim();
      if (source === "text") {
        await embedElement(instance, {
          graphElementId: element.id,
          text,
          name: trimmedName || undefined,
        });
        return;
      }
      const parsed = parseVector(vectorText);
      if (!parsed.ok) throw new Error(`Vector: ${parsed.error}.`);
      await putElementEmbedding(instance, element.id, trimmedName, { vector: parsed.vector });
    },
    onSuccess: () => {
      setVectorText("");
      setText("");
      onRefresh();
    },
  });

  const remove = useMutation({
    mutationFn: (embeddingName: string) =>
      deleteElementEmbedding(instance, element.id, embeddingName),
    onSuccess: onRefresh,
  });

  return (
    <div className="space-y-3" data-testid="embeddings-tab">
      {embeddings.length === 0 ? (
        <div className="text-fg-faint">no embeddings on this element</div>
      ) : (
        <table className="w-full">
          <thead>
            <tr className="text-fg-faint">
              <th className="table-cell">name</th>
              <th className="table-cell">vector</th>
              <th className="table-cell">model</th>
              <th className="table-cell" />
            </tr>
          </thead>
          <tbody>
            {embeddings.map((e) => (
              <tr key={e.name} data-testid={`embedding-row-${e.name}`}>
                <td className="table-cell font-semibold">{e.name}</td>
                <td className="table-cell font-mono">{previewVector(e.value)}</td>
                <td className="table-cell text-fg-dim">
                  {typeof e.model === "string" && e.model ? e.model : "—"}
                </td>
                <td className="table-cell">
                  <button
                    type="button"
                    className="btn btn-danger"
                    data-testid={`embedding-remove-${e.name}`}
                    disabled={remove.isPending}
                    onClick={() => remove.mutate(e.name)}
                  >
                    Remove
                  </button>
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      )}

      <form
        className="border-line flex flex-wrap items-end gap-2 border-t pt-3"
        onSubmit={(event) => {
          event.preventDefault();
          write.mutate();
        }}
      >
        <Field helpKey="embeddingName" label="name" htmlFor="emb-name">
          <input
            id="emb-name"
            data-testid="emb-name"
            className="input w-28"
            value={name}
            onChange={(event) => setName(event.target.value)}
          />
        </Field>
        <div className="border-line flex overflow-hidden rounded border">
          {(["vector", "text"] as const).map((mode) => (
            <button
              key={mode}
              type="button"
              data-testid={`emb-source-${mode}`}
              className={`px-2 py-1 text-[11px] ${
                source === mode ? "bg-panel-2 text-accent" : "text-fg-dim hover:text-fg"
              }`}
              onClick={() => setSource(mode)}
            >
              {mode}
            </button>
          ))}
        </div>
        {source === "vector" ? (
          <Field
            helpKey="embeddingVectorPaste"
            label="vector"
            htmlFor="emb-vector"
            className="grow"
          >
            <input
              id="emb-vector"
              data-testid="emb-vector"
              className="input w-full font-mono"
              value={vectorText}
              onChange={(event) => setVectorText(event.target.value)}
              placeholder="[0.12, -0.5, 0.33]"
            />
          </Field>
        ) : (
          <Field helpKey="embeddingText" label="text" htmlFor="emb-text" className="grow">
            <input
              id="emb-text"
              data-testid="emb-text"
              className="input w-full"
              value={text}
              disabled={textUnavailable}
              onChange={(event) => setText(event.target.value)}
              placeholder="a red bicycle"
            />
            {textUnavailable && (
              <div className="text-warn text-[11px]" data-testid="emb-text-unavailable">
                {providerEnabled === null
                  ? "provider status unknown — Compute the Graph shape (Analytics), or paste a vector."
                  : "the embedding provider is off on this instance — paste a vector instead."}
              </div>
            )}
          </Field>
        )}
        <button
          type="submit"
          className="btn btn-accent"
          data-testid="emb-write"
          disabled={
            !name.trim() ||
            write.isPending ||
            (source === "vector" ? !vectorText.trim() : textUnavailable || !text.trim())
          }
        >
          {write.isPending ? "Writing…" : "Set embedding"}
        </button>
      </form>
      {(write.isError || remove.isError) && (
        <ErrorBox error={write.error ?? remove.error} />
      )}
    </div>
  );
}

function PropertiesTab({ properties }: { properties: PropertyREST[] }) {
  const [showReserved, setShowReserved] = useState(false);
  const visible = showReserved
    ? properties
    : properties.filter((p) => !isReservedEmbeddingProperty(p.propertyId));
  const hasReserved = properties.some((p) => isReservedEmbeddingProperty(p.propertyId));

  return (
    <div className="space-y-2" data-testid="properties-tab">
      <table className="w-full">
        <thead>
          <tr className="text-fg-faint">
            <th className="table-cell">property</th>
            <th className="table-cell">value</th>
            <th className="table-cell">type</th>
          </tr>
        </thead>
        <tbody>
          {visible.map((p) => (
            <tr key={p.propertyId}>
              <td className="table-cell">{p.propertyId}</td>
              <td className="table-cell">
                {isReservedEmbeddingProperty(p.propertyId)
                  ? previewVector(p.propertyValue)
                  : formatPropertyValue(p.propertyValue)}
              </td>
              <td className="table-cell text-fg-dim">{p.fullQualifiedTypeName ?? "—"}</td>
            </tr>
          ))}
          {visible.length === 0 && (
            <tr>
              <td className="table-cell text-fg-faint" colSpan={3}>
                no properties
              </td>
            </tr>
          )}
        </tbody>
      </table>
      {hasReserved && (
        <label
          className="text-fg-dim label-help flex items-center gap-1 text-[11px]"
          title={help("embeddingShowReserved")}
        >
          <input
            type="checkbox"
            data-testid="show-reserved"
            checked={showReserved}
            onChange={(event) => setShowReserved(event.target.checked)}
          />
          show reserved embedding properties (folded into the Embeddings tab)
        </label>
      )}
    </div>
  );
}

function ElementDetail({
  element,
  providerEnabled,
  onRefresh,
}: {
  element: VertexREST | EdgeREST;
  providerEnabled: boolean | null;
  onRefresh: () => void;
}) {
  const instance = useActiveInstance()!;
  const edge = isEdge(element) ? element : null;
  const [tab, setTab] = useState<"properties" | "embeddings">("properties");

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
        <div className="border-line flex gap-1 border-b">
          {(["properties", "embeddings"] as const).map((t) => (
            <button
              key={t}
              type="button"
              data-testid={`element-tab-${t}`}
              className={`px-2 py-1 text-[11px] tracking-wider uppercase ${
                tab === t
                  ? "border-accent text-accent border-b-2"
                  : "text-fg-dim hover:text-fg"
              }`}
              onClick={() => setTab(t)}
            >
              {t}
            </button>
          ))}
        </div>
        {tab === "properties" ? (
          <PropertiesTab properties={element.properties ?? []} />
        ) : (
          <EmbeddingsTab
            instance={instance}
            element={element}
            providerEnabled={providerEnabled}
            onRefresh={onRefresh}
          />
        )}
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

  const provider = embeddingProvider(useGraphShape(instance).data);
  const providerEnabled = provider ? provider.enabled : null;

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
          />
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
