import { useEffect, useState } from "react";
import { useMutation, useQueryClient } from "@tanstack/react-query";
import { useActiveInstance } from "../instances/registry";
import { embeddingProvider, shapeSuggestions, useGraphShape } from "../state/graphShape";
import { useStatus } from "../state/status";
import {
  addVectorToIndex,
  createIndex,
  deleteIndex,
  embeddingSearch,
  scanFulltext,
  scanIndex,
  scanIndexRange,
  scanProperty,
  scanSpatial,
  scanVector,
} from "../api/endpoints";
import type {
  EdgeREST,
  FulltextSearchResultREST,
  VectorIndexAddSpecification,
  VectorSearchResultREST,
  VertexREST,
} from "../api/types";
import { BINARY_OPERATORS, type BinaryOperatorName } from "../api/types";
import { toLiteral, type TypedValue } from "../lib/literals";
import { parseVector } from "../lib/vector";
import { hydrateElements, isEdge, type HydrationProgress } from "../lib/hydrate";
import { TypedLiteralEditor } from "../components/TypedLiteralEditor";
import { Field } from "../components/Field";
import { help } from "../lib/fieldHelp";
import { ElementTable } from "../components/ElementTable";
import { ErrorBox } from "../components/ErrorBox";
import { getInstanceStore } from "../state/instanceStore";

/**
 * Query workspace (FR-8/9/10/11): the scan types with typed literals, id-list
 * hydration with progress, open-as-table + send-to-canvas, and index management.
 * Identifier inputs are free-form with <datalist> suggestions: index ids come live from
 * /status (feature studio-index-discovery); property keys and labels from the Graph
 * shape snapshot (feature studio-coverage).
 */

type ScanKind = "property" | "index" | "range" | "fulltext" | "spatial" | "vector";

const VECTOR_KINDS = ["any", "vertex", "edge"] as const;

const OPERATORS = Object.keys(BINARY_OPERATORS) as BinaryOperatorName[];

const RESULT_TYPES = ["Vertices", "Edges", "Both"] as const;

export function QueryScreen() {
  const instance = useActiveInstance()!;
  const store = getInstanceStore(instance.id);
  const mergeIntoCanvas = store((s) => s.mergeIntoCanvas);
  const addResultSet = store((s) => s.addResultSet);

  const [kind, setKind] = useState<ScanKind>("property");
  const [propertyId, setPropertyId] = useState("");
  const [indexId, setIndexId] = useState("");
  const [operator, setOperator] = useState<BinaryOperatorName>("Equals");
  const [resultType, setResultType] =
    useState<(typeof RESULT_TYPES)[number]>("Both");
  const [literal, setLiteral] = useState<TypedValue>({ type: "System.String", raw: "" });
  const [leftLimit, setLeftLimit] = useState<TypedValue>({ type: "System.Int32", raw: "0" });
  const [rightLimit, setRightLimit] = useState<TypedValue>({
    type: "System.Int32",
    raw: "100",
  });
  const [includeLeft, setIncludeLeft] = useState(true);
  const [includeRight, setIncludeRight] = useState(true);
  const [fulltextQuery, setFulltextQuery] = useState("");
  const [spatialElementId, setSpatialElementId] = useState("");
  const [spatialDistance, setSpatialDistance] = useState("10");
  const [vectorText, setVectorText] = useState("");
  const [vectorK, setVectorK] = useState("10");
  const [vectorKind, setVectorKind] = useState<(typeof VECTOR_KINDS)[number]>("any");
  const [vectorLabel, setVectorLabel] = useState("");
  // Semantic search (feature embedding-provider): embed a query TEXT server-side, then kNN.
  const [vectorSource, setVectorSource] = useState<"vector" | "text">("vector");
  const [vectorSearchText, setVectorSearchText] = useState("");

  const [progress, setProgress] = useState<HydrationProgress | null>(null);
  const [elements, setElements] = useState<(VertexREST | EdgeREST)[]>([]);
  const [fulltextResult, setFulltextResult] = useState<FulltextSearchResultREST | null>(null);
  const [vectorResult, setVectorResult] = useState<VectorSearchResultREST | null>(null);
  const [idCount, setIdCount] = useState<number | null>(null);
  const [capped, setCapped] = useState(false);

  const shape = useGraphShape(instance).data;
  const suggestions = shapeSuggestions(shape);
  const provider = embeddingProvider(shape);
  const providerEnabled = provider ? provider.enabled : null;

  // Index-id suggestions: the live /status inventory first, shape-snapshot ids as backup
  // (the snapshot may know ids from before a reconnect; the union keeps both honest).
  const status = useStatus(instance);
  const indexIdOptions = [
    ...new Set([
      ...(status.data?.indices ?? []).map((i) => i.indexId).filter(Boolean),
      ...suggestions.indexIds,
    ]),
  ];

  // Consume a one-shot prefill (e.g. Graph shape index row → "Scan").
  const scanPrefill = store((s) => s.scanPrefill);
  const setScanPrefill = store((s) => s.setScanPrefill);
  useEffect(() => {
    if (scanPrefill) {
      setKind(scanPrefill.kind);
      setIndexId(scanPrefill.indexId);
      setScanPrefill(null);
    }
  }, [scanPrefill, setScanPrefill]);

  const scan = useMutation({
    mutationFn: async () => {
      setElements([]);
      setFulltextResult(null);
      setVectorResult(null);
      setIdCount(null);
      setCapped(false);
      setProgress(null);

      let ids: number[] = [];
      if (kind === "property") {
        ids =
          (await scanProperty(instance, propertyId, {
            operator: BINARY_OPERATORS[operator],
            literal: toLiteral(literal),
            resultType,
          })) ?? [];
      } else if (kind === "index") {
        ids =
          (await scanIndex(instance, {
            indexId,
            operator: BINARY_OPERATORS[operator],
            literal: toLiteral(literal),
            resultType,
          })) ?? [];
      } else if (kind === "range") {
        ids =
          (await scanIndexRange(instance, {
            indexId,
            leftLimit: toLiteral(leftLimit),
            rightLimit: toLiteral(rightLimit),
            includeLeft,
            includeRight,
            resultType,
          })) ?? [];
      } else if (kind === "fulltext") {
        const result = await scanFulltext(instance, {
          indexId,
          requestString: fulltextQuery,
        });
        setFulltextResult(result);
        ids = result?.elements.map((e) => e.graphElementId) ?? [];
      } else if (kind === "vector") {
        let result: VectorSearchResultREST | null;
        if (vectorSource === "text") {
          // Semantic search: the provider embeds the text once, server-side.
          result = await embeddingSearch(instance, {
            indexId,
            text: vectorSearchText,
            k: Number(vectorK),
            kind: vectorKind === "any" ? undefined : vectorKind,
            label: vectorLabel || undefined,
          });
        } else {
          const parsed = parseVector(vectorText);
          if (!parsed.ok) {
            throw new Error(`Query vector: ${parsed.error}.`);
          }
          result = await scanVector(instance, {
            indexId,
            query: parsed.vector,
            k: Number(vectorK),
            kind: vectorKind === "any" ? undefined : vectorKind,
            label: vectorLabel || undefined,
          });
        }
        setVectorResult(result);
        ids = result?.results?.map((r) => r.graphElementId) ?? [];
      } else {
        ids =
          (await scanSpatial(instance, {
            indexId,
            graphElementId: Number(spatialElementId),
            distance: Number(spatialDistance),
          })) ?? [];
      }

      setIdCount(ids.length);
      addResultSet(`${kind} scan (${ids.length} ids)`, ids);
      const hydrated = await hydrateElements(instance, ids, { onProgress: setProgress });
      setCapped(hydrated.capped);
      return hydrated.elements;
    },
    onSuccess: (hydrated) => setElements(hydrated),
    onSettled: () => setProgress(null),
  });

  const needsIndex = kind !== "property";
  const needsLiteral = kind === "property" || kind === "index";
  const parsedVector = kind === "vector" ? parseVector(vectorText) : null;

  return (
    <div className="mx-auto max-w-5xl space-y-4">
      <section className="panel">
        <div className="panel-title">Scan</div>
        <form
          className="space-y-3 p-3"
          onSubmit={(e) => {
            e.preventDefault();
            scan.mutate();
          }}
        >
          <div className="flex flex-wrap items-end gap-3">
            <Field helpKey="scanKind" label="scan type" htmlFor="scan-kind">
              <select
                id="scan-kind"
                data-testid="scan-kind"
                className="input w-auto"
                value={kind}
                onChange={(e) => setKind(e.target.value as ScanKind)}
              >
                <option value="property">property scan</option>
                <option value="index">index scan</option>
                <option value="range">index range</option>
                <option value="fulltext">fulltext</option>
                <option value="spatial">spatial</option>
                <option value="vector">vector (kNN)</option>
              </select>
            </Field>

            {kind === "property" && (
              <Field helpKey="propertyId" label="property id" htmlFor="scan-property">
                <input
                  id="scan-property"
                  data-testid="scan-property"
                  className="input w-40"
                  list="shape-property-keys"
                  value={propertyId}
                  onChange={(e) => setPropertyId(e.target.value)}
                  placeholder="age"
                />
              </Field>
            )}

            {needsIndex && (
              <Field helpKey="indexId" label="index id" htmlFor="scan-index">
                <input
                  id="scan-index"
                  className="input w-40"
                  list="shape-index-ids"
                  value={indexId}
                  onChange={(e) => setIndexId(e.target.value)}
                  placeholder="myIndex"
                />
              </Field>
            )}

            {needsLiteral && (
              <>
                <Field helpKey="scanOperator" label="operator" htmlFor="scan-operator">
                  <select
                    id="scan-operator"
                    className="input w-auto"
                    value={operator}
                    onChange={(e) => setOperator(e.target.value as BinaryOperatorName)}
                  >
                    {OPERATORS.map((op) => (
                      <option key={op}>{op}</option>
                    ))}
                  </select>
                </Field>
                <TypedLiteralEditor
                  helpKey="scanLiteral"
                  label="literal"
                  idPrefix="scan-literal"
                  value={literal}
                  onChange={setLiteral}
                />
              </>
            )}

            {kind === "range" && (
              <>
                <TypedLiteralEditor
                  helpKey="rangeLeftLimit"
                  label="left limit"
                  idPrefix="range-left"
                  value={leftLimit}
                  onChange={setLeftLimit}
                />
                <TypedLiteralEditor
                  helpKey="rangeRightLimit"
                  label="right limit"
                  idPrefix="range-right"
                  value={rightLimit}
                  onChange={setRightLimit}
                />
                <label
                  className="text-fg-dim label-help flex items-center gap-1 text-[12px]"
                  title={help("rangeIncludeLeft")}
                >
                  <input
                    type="checkbox"
                    checked={includeLeft}
                    onChange={(e) => setIncludeLeft(e.target.checked)}
                  />
                  incl. left
                </label>
                <label
                  className="text-fg-dim label-help flex items-center gap-1 text-[12px]"
                  title={help("rangeIncludeRight")}
                >
                  <input
                    type="checkbox"
                    checked={includeRight}
                    onChange={(e) => setIncludeRight(e.target.checked)}
                  />
                  incl. right
                </label>
              </>
            )}

            {kind === "fulltext" && (
              <Field
                helpKey="fulltextQuery"
                label="query"
                htmlFor="fulltext-query"
                className="grow"
              >
                <input
                  id="fulltext-query"
                  className="input"
                  value={fulltextQuery}
                  onChange={(e) => setFulltextQuery(e.target.value)}
                  placeholder="search text"
                />
              </Field>
            )}

            {kind === "spatial" && (
              <>
                <Field
                  helpKey="spatialElementId"
                  label="element id"
                  htmlFor="spatial-element"
                >
                  <input
                    id="spatial-element"
                    className="input w-28"
                    value={spatialElementId}
                    onChange={(e) => setSpatialElementId(e.target.value)}
                  />
                </Field>
                <Field
                  helpKey="spatialDistance"
                  label="distance"
                  htmlFor="spatial-distance"
                >
                  <input
                    id="spatial-distance"
                    className="input w-28"
                    value={spatialDistance}
                    onChange={(e) => setSpatialDistance(e.target.value)}
                  />
                </Field>
              </>
            )}

            {kind === "vector" && (
              <>
                <div className="flex basis-full items-center gap-2">
                  <span className="text-fg-faint text-[10px] tracking-widest uppercase">
                    query by
                  </span>
                  <div className="border-line flex overflow-hidden rounded border">
                    {(["vector", "text"] as const).map((mode) => (
                      <button
                        key={mode}
                        type="button"
                        data-testid={`vector-source-${mode}`}
                        className={`px-2 py-1 text-[11px] ${
                          vectorSource === mode
                            ? "bg-panel-2 text-accent"
                            : "text-fg-dim hover:text-fg"
                        } ${mode === "text" && providerEnabled !== true ? "opacity-50" : ""}`}
                        disabled={mode === "text" && providerEnabled !== true}
                        title={
                          mode === "text" && providerEnabled !== true
                            ? providerEnabled === null
                              ? "provider status unknown — Compute the Graph shape (Analytics)"
                              : "the embedding provider is off on this instance"
                            : undefined
                        }
                        onClick={() => setVectorSource(mode)}
                      >
                        {mode === "text" ? "text (provider)" : "vector"}
                      </button>
                    ))}
                  </div>
                </div>
                {vectorSource === "text" ? (
                  <Field
                    helpKey="embeddingSearchText"
                    label="query text"
                    htmlFor="vector-search-text"
                    className="grow basis-full"
                  >
                    <input
                      id="vector-search-text"
                      data-testid="vector-search-text"
                      className="input w-full"
                      value={vectorSearchText}
                      onChange={(e) => setVectorSearchText(e.target.value)}
                      placeholder="red bicycles"
                    />
                    <div className="text-fg-faint text-[11px]">
                      embedded once server-side, then kNN — scores identical to a pasted vector
                    </div>
                  </Field>
                ) : (
                  <Field
                    helpKey="vectorQuery"
                    label="query vector (JSON array or comma-separated floats)"
                    htmlFor="vector-query"
                    className="grow basis-full"
                  >
                    <textarea
                      id="vector-query"
                      data-testid="vector-query"
                      className="input h-16 w-full font-mono"
                      value={vectorText}
                      onChange={(e) => setVectorText(e.target.value)}
                      placeholder="[0.12, -0.5, 0.33]"
                    />
                    <div className="text-fg-faint text-[11px]" data-testid="vector-dimension">
                      {!vectorText.trim()
                        ? "paste the embedding your pipeline logged"
                        : parsedVector?.ok
                          ? `d=${parsedVector.vector.length} — must match the index dimension`
                          : parsedVector?.error}
                    </div>
                  </Field>
                )}
                <Field helpKey="vectorK" label="k (1–1024)" htmlFor="vector-k">
                  <input
                    id="vector-k"
                    className="input w-20"
                    type="number"
                    min={1}
                    max={1024}
                    value={vectorK}
                    onChange={(e) => setVectorK(e.target.value)}
                  />
                </Field>
                <Field helpKey="vectorKind" label="element kind" htmlFor="vector-kind">
                  <select
                    id="vector-kind"
                    className="input w-auto"
                    value={vectorKind}
                    onChange={(e) =>
                      setVectorKind(e.target.value as (typeof VECTOR_KINDS)[number])
                    }
                  >
                    {VECTOR_KINDS.map((k) => (
                      <option key={k}>{k}</option>
                    ))}
                  </select>
                </Field>
                <Field
                  helpKey="vectorLabelConstraint"
                  label="label constraint"
                  htmlFor="vector-label"
                >
                  <input
                    id="vector-label"
                    className="input w-32"
                    list="shape-labels"
                    value={vectorLabel}
                    onChange={(e) => setVectorLabel(e.target.value)}
                    placeholder="person"
                  />
                </Field>
              </>
            )}

            {needsLiteral && (
              <Field
                helpKey="scanResultType"
                label="result type"
                htmlFor="scan-result-type"
              >
                <select
                  id="scan-result-type"
                  className="input w-auto"
                  value={resultType}
                  onChange={(e) =>
                    setResultType(e.target.value as (typeof RESULT_TYPES)[number])
                  }
                >
                  {RESULT_TYPES.map((rt) => (
                    <option key={rt}>{rt}</option>
                  ))}
                </select>
              </Field>
            )}

            <button
              type="submit"
              className="btn btn-accent"
              data-testid="scan-run"
              disabled={
                scan.isPending ||
                (kind === "vector" &&
                  (vectorSource === "text"
                    ? !vectorSearchText.trim() || providerEnabled !== true
                    : !parsedVector?.ok))
              }
            >
              {scan.isPending ? "Scanning…" : "Run scan"}
            </button>
          </div>

          {progress && (
            <div className="text-fg-dim text-[12px]" data-testid="hydration-progress">
              hydrating {progress.done}/{progress.total}…
            </div>
          )}
        </form>
        {scan.isError && (
          <div className="px-3 pb-3">
            <ErrorBox error={scan.error} />
          </div>
        )}
      </section>

      {idCount !== null && (
        <section className="panel">
          <div className="panel-title">
            results — {idCount} ids
            {vectorResult && (
              <span className="text-fg-faint normal-case" data-testid="vector-legend">
                {vectorResult.metric ?? "?"} ·{" "}
                {vectorResult.higherIsBetter ? "higher is better" : "lower is better"}
              </span>
            )}
            {capped && <span className="text-warn">(hydration capped at 500)</span>}
            <button
              type="button"
              className="btn btn-accent ml-auto"
              data-testid="send-to-canvas"
              disabled={elements.length === 0}
              onClick={() =>
                mergeIntoCanvas(
                  elements.filter((e): e is VertexREST => !isEdge(e)),
                  elements.filter(isEdge),
                )
              }
            >
              Send to canvas
            </button>
          </div>
          {fulltextResult && (
            <div className="border-line border-b p-3 text-[12px]">
              <div className="text-fg-faint mb-1 text-[10px] tracking-widest uppercase">
                highlights (max score {fulltextResult.maximumScore.toFixed(2)})
              </div>
              {fulltextResult.elements.slice(0, 20).map((el) => (
                <div key={el.graphElementId} className="text-fg-dim">
                  #{el.graphElementId} ({el.score.toFixed(2)}): {el.highlights.join(" … ")}
                </div>
              ))}
            </div>
          )}
          <ElementTable
            elements={elements}
            scores={
              vectorResult
                ? new Map(
                    (vectorResult.results ?? []).map((r) => [r.graphElementId, r.score]),
                  )
                : undefined
            }
            scoreHeader={vectorResult?.metric?.toLowerCase() ?? "score"}
          />
        </section>
      )}

      <IndexManagement />

      {/* Shared identifier suggestions from the Graph shape snapshot (empty until computed). */}
      <datalist id="shape-property-keys">
        {suggestions.propertyKeys.map((key) => (
          <option key={key} value={key} />
        ))}
      </datalist>
      <datalist id="shape-index-ids">
        {indexIdOptions.map((id) => (
          <option key={id} value={id} />
        ))}
      </datalist>
      <datalist id="shape-labels">
        {[...new Set([...suggestions.vertexLabels, ...suggestions.edgeLabels])].map(
          (label) => (
            <option key={label} value={label} />
          ),
        )}
      </datalist>
      {/* Embedding names seen on the graph (feature element-embeddings), for the bound-index
          binding input; empty until a Graph shape is computed. */}
      <datalist id="shape-embedding-names">
        {suggestions.embeddingNames.map((name) => (
          <option key={name} value={name} />
        ))}
      </datalist>
    </div>
  );
}

function IndexManagement() {
  const instance = useActiveInstance()!;
  const queryClient = useQueryClient();
  const [indexId, setIndexId] = useState("");
  const [indexType, setIndexType] = useState("DictionaryIndex");
  const [message, setMessage] = useState<string | null>(null);
  const [dimension, setDimension] = useState("384");
  const [metric, setMetric] = useState("Cosine");
  const [embeddingName, setEmbeddingName] = useState("");
  const [model, setModel] = useState("");
  const [showVectorAdd, setShowVectorAdd] = useState(false);
  const [vaElementId, setVaElementId] = useState("");
  const [vaMode, setVaMode] = useState<"property" | "explicit">("property");
  const [vaPropertyId, setVaPropertyId] = useState("embedding");
  const [vaVectorText, setVaVectorText] = useState("");

  // The create dropdown feeds on the server's plugin discovery (free-form fallback for
  // servers predating the field / an unreachable status).
  const status = useStatus(instance).data;
  const availableTypes = status?.availableIndexPlugins ?? [];
  const inventory = status?.indices ?? [];
  const refreshInventory = () =>
    queryClient.invalidateQueries({ queryKey: [instance.id, "status"] });

  const isVectorIndex = indexType.trim() === "VectorIndex";
  // A vector index BOUND to an embedding (feature element-embeddings) maintains itself —
  // the add-vector form is rejected against it (400), so it is disabled with the reason.
  const targetBinding = inventory.find(
    (i) => i.indexId === indexId.trim() && i.embeddingName,
  );
  // SpatialIndex.Initialize needs CLR objects (metric, dimensions) that JSON plugin
  // options cannot carry — POST /index always answers false for it, so Create is gated
  // (pinned by StatusIndexInventoryTest; spec studio-index-discovery).
  const isSpatialIndex = indexType.trim() === "SpatialIndex";

  const create = useMutation({
    mutationFn: () =>
      createIndex(instance, {
        uniqueId: indexId,
        pluginType: indexType,
        // VectorIndex options travel as typed literals (vector-index README §creation).
        // embeddingName/model are added only when set, so a raw index stays exactly the
        // old two-option shape (pinned by index-management.test.tsx).
        pluginOptions: isVectorIndex
          ? {
              dimension: {
                propertyId: "dimension",
                propertyValue: dimension,
                fullQualifiedTypeName: "System.Int32",
              },
              metric: {
                propertyId: "metric",
                propertyValue: metric,
                fullQualifiedTypeName: "System.String",
              },
              ...(embeddingName.trim()
                ? {
                    embeddingName: {
                      propertyId: "embeddingName",
                      propertyValue: embeddingName.trim(),
                      fullQualifiedTypeName: "System.String",
                    },
                  }
                : {}),
              ...(model.trim()
                ? {
                    model: {
                      propertyId: "model",
                      propertyValue: model.trim(),
                      fullQualifiedTypeName: "System.String",
                    },
                  }
                : {}),
            }
          : undefined,
      }),
    onSuccess: (ok) => {
      setMessage(
        ok
          ? `Index '${indexId}' created.`
          : `Index '${indexId}' was NOT created — the id may already exist or the options are invalid.`,
      );
      if (ok) refreshInventory();
    },
  });
  const remove = useMutation({
    mutationFn: () => deleteIndex(instance, indexId),
    onSuccess: () => {
      setMessage(`Index '${indexId}' deleted.`);
      refreshInventory();
    },
  });

  const vectorAdd = useMutation({
    mutationFn: () => {
      const spec: VectorIndexAddSpecification = {
        graphElementId: Number(vaElementId),
      };
      if (vaMode === "property") {
        spec.propertyId = vaPropertyId.trim();
      } else {
        const parsed = parseVector(vaVectorText);
        if (!parsed.ok) throw new Error(`Vector: ${parsed.error}.`);
        spec.vector = parsed.vector;
      }
      return addVectorToIndex(instance, indexId, spec);
    },
    onSuccess: (ok) =>
      setMessage(
        ok
          ? `Vector for element ${vaElementId} added to '${indexId}'.`
          : `Element ${vaElementId} was not added — check the element id, property, and dimension.`,
      ),
  });

  return (
    <section className="panel">
      <div className="panel-title">Index management</div>
      {inventory.length > 0 && (
        <div className="border-line flex flex-wrap gap-2 border-b p-3" data-testid="index-inventory">
          {inventory.map((index) => (
            <span
              key={index.indexId}
              className="border-line text-fg-dim rounded border px-2 py-0.5 text-[11px]"
            >
              <button
                type="button"
                className="text-accent-2 hover:underline"
                onClick={() => setIndexId(index.indexId)}
              >
                {index.indexId}
              </button>{" "}
              <span className="text-fg-faint">{index.pluginType}</span>
              {index.embeddingName && (
                <span
                  className="border-accent/50 text-accent ml-1 rounded border px-1"
                  data-testid={`index-bound-${index.indexId}`}
                  title={`self-maintained projection of the '${index.embeddingName}' element embedding — explicit adds are rejected`}
                >
                  bound:{index.embeddingName}
                </span>
              )}
            </span>
          ))}
        </div>
      )}
      <div className="flex flex-wrap items-end gap-2 p-3">
        <Field helpKey="indexId" label="index id" htmlFor="index-id">
          <input
            id="index-id"
            className="input w-40"
            list="shape-index-ids"
            value={indexId}
            onChange={(e) => setIndexId(e.target.value)}
            placeholder="myIndex"
          />
        </Field>
        <Field helpKey="indexPluginType" label="plugin type" htmlFor="index-type">
          {availableTypes.length > 0 ? (
            <select
              id="index-type"
              data-testid="index-type"
              className="input w-48"
              value={indexType}
              onChange={(e) => setIndexType(e.target.value)}
            >
              {!availableTypes.includes(indexType) && <option>{indexType}</option>}
              {availableTypes.map((t) => (
                <option key={t}>{t}</option>
              ))}
            </select>
          ) : (
            <input
              id="index-type"
              data-testid="index-type"
              className="input w-48"
              value={indexType}
              onChange={(e) => setIndexType(e.target.value)}
              placeholder="DictionaryIndex"
            />
          )}
        </Field>
        {isVectorIndex && (
          <>
            <Field
              helpKey="vectorDimension"
              label="dimension (1–4096)"
              htmlFor="vector-dimension-opt"
            >
              <input
                id="vector-dimension-opt"
                className="input w-24"
                type="number"
                min={1}
                max={4096}
                value={dimension}
                onChange={(e) => setDimension(e.target.value)}
              />
            </Field>
            <Field helpKey="vectorMetric" label="metric" htmlFor="vector-metric">
              <select
                id="vector-metric"
                className="input w-auto"
                value={metric}
                onChange={(e) => setMetric(e.target.value)}
              >
                <option>Cosine</option>
                <option>DotProduct</option>
                <option>L2</option>
              </select>
            </Field>
            <Field
              helpKey="vectorBindEmbeddingName"
              label="bind embedding (optional)"
              htmlFor="vector-embedding-name"
            >
              <input
                id="vector-embedding-name"
                data-testid="vector-embedding-name"
                className="input w-32"
                list="shape-embedding-names"
                value={embeddingName}
                onChange={(e) => setEmbeddingName(e.target.value)}
                placeholder="default"
              />
            </Field>
            <Field helpKey="vectorModel" label="model (optional)" htmlFor="vector-model">
              <input
                id="vector-model"
                className="input w-40"
                value={model}
                onChange={(e) => setModel(e.target.value)}
                placeholder="bge-micro-v2#384#Cosine"
              />
            </Field>
          </>
        )}
        <button
          type="button"
          className="btn btn-accent"
          disabled={!indexId.trim() || create.isPending || isSpatialIndex}
          onClick={() => create.mutate()}
        >
          Create
        </button>
        <button
          type="button"
          className="btn btn-danger"
          disabled={!indexId.trim() || remove.isPending}
          onClick={() => remove.mutate()}
        >
          Delete
        </button>
        {message && <span className="text-accent text-[12px]">{message}</span>}
        {isSpatialIndex ? (
          <p className="text-warn basis-full text-[11px]" data-testid="spatial-create-note">
            SpatialIndex cannot be created over REST — its configuration (metric, space
            dimensions) is not expressible as JSON plugin options. Delete still works.
          </p>
        ) : (
          !isVectorIndex && (
            <span className="text-fg-faint text-[11px]" data-testid="no-options-note">
              this index type takes no creation options
            </span>
          )
        )}
        {isVectorIndex && embeddingName.trim() && (
          <p className="text-fg-faint basis-full text-[11px]" data-testid="bound-create-note">
            Bound: this index auto-maintains a projection of the '{embeddingName.trim()}'
            element embedding — write embeddings on the elements (Browser → Embeddings) and
            the index follows; explicit vector-adds are rejected.
          </p>
        )}
      </div>
      <div className="px-3 pb-3">
        <button
          type="button"
          className="btn"
          data-testid="toggle-vector-add"
          onClick={() => setShowVectorAdd((s) => !s)}
        >
          {showVectorAdd ? "Hide" : "Show"} vector add
        </button>
        {showVectorAdd && (
          <div className="mt-2 flex flex-wrap items-end gap-2" data-testid="vector-add">
            <Field helpKey="vectorAddElementId" label="element id" htmlFor="va-element">
              <input
                id="va-element"
                className="input w-28"
                value={vaElementId}
                onChange={(e) => setVaElementId(e.target.value)}
              />
            </Field>
            <div className="border-line flex overflow-hidden rounded border">
              {(["property", "explicit"] as const).map((mode) => (
                <button
                  key={mode}
                  type="button"
                  data-testid={`vector-add-mode-${mode}`}
                  className={`px-2 py-1 text-[11px] ${
                    vaMode === mode
                      ? "bg-panel-2 text-accent"
                      : "text-fg-dim hover:text-fg"
                  }`}
                  onClick={() => setVaMode(mode)}
                >
                  {mode}
                </button>
              ))}
            </div>
            {vaMode === "property" ? (
              <Field
                helpKey="vectorAddPropertyId"
                label="property id"
                htmlFor="va-property"
              >
                <input
                  id="va-property"
                  className="input w-32"
                  list="shape-property-keys"
                  value={vaPropertyId}
                  onChange={(e) => setVaPropertyId(e.target.value)}
                  placeholder="embedding"
                />
              </Field>
            ) : (
              <Field
                helpKey="vectorAddVector"
                label="vector"
                htmlFor="va-vector"
                className="grow"
              >
                <input
                  id="va-vector"
                  className="input w-full font-mono"
                  value={vaVectorText}
                  onChange={(e) => setVaVectorText(e.target.value)}
                  placeholder="[0.12, -0.5, 0.33]"
                />
              </Field>
            )}
            <button
              type="button"
              className="btn btn-accent"
              disabled={
                !indexId.trim() ||
                !vaElementId.trim() ||
                Boolean(targetBinding) ||
                (vaMode === "property" ? !vaPropertyId.trim() : !vaVectorText.trim()) ||
                vectorAdd.isPending
              }
              title={
                targetBinding
                  ? `'${targetBinding.indexId}' is bound to embedding '${targetBinding.embeddingName}' — write the element embedding instead`
                  : undefined
              }
              onClick={() => vectorAdd.mutate()}
            >
              Add vector
            </button>
            {targetBinding ? (
              <p className="text-warn basis-full text-[11px]" data-testid="bound-add-note">
                '{targetBinding.indexId}' is a bound projection of the '
                {targetBinding.embeddingName}' embedding and maintains itself — set the
                embedding on the element (Browser → Embeddings) rather than adding to the
                index.
              </p>
            ) : (
              <p className="text-fg-faint basis-full text-[11px]">
                targets the index id above · property mode is WAL-recoverable — the honest
                default; bulk embedding loads belong to your pipeline, not a browser.
              </p>
            )}
          </div>
        )}
      </div>
      {(create.isError || remove.isError || vectorAdd.isError) && (
        <div className="px-3 pb-3">
          <ErrorBox error={create.error ?? remove.error ?? vectorAdd.error} />
        </div>
      )}
      <p className="text-fg-faint px-3 pb-3 text-[11px]">
        Index ids and plugin types are suggested live from the server. Property and label
        suggestions still need a Graph shape snapshot (Analytics screen → Compute).
      </p>
    </section>
  );
}
