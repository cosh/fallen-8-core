import { useEffect, useState } from "react";
import { useMutation } from "@tanstack/react-query";
import { useInstanceStore } from "../instances/registry";
import { shapeSuggestions, useEmbeddingProvider, useGraphShape } from "../state/graphShape";
import { useStatus } from "../state/status";
import {
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
  VectorSearchResultREST,
  VertexREST,
} from "../api/types";
import { BINARY_OPERATORS, type BinaryOperatorName } from "../api/types";
import { toLiteral, type TypedValue } from "../lib/literals";
import { parseVector } from "../lib/vector";
import {
  indexCapabilities,
  type IndexCapability,
} from "../lib/indexCapabilities";
import { hydrateElements, isEdge, type HydrationProgress } from "../lib/hydrate";
import { TypedLiteralEditor } from "../components/TypedLiteralEditor";
import { Field } from "../components/Field";
import { help } from "../lib/fieldHelp";
import { ElementTable } from "../components/ElementTable";
import { ErrorBox } from "../components/ErrorBox";
/**
 * Query workspace (FR-8/9/11, re-shaped by feature index-workspace): either a property
 * scan (the index-less path) or an index query. Index queries are INDEX-FIRST: pick the
 * index from the live /status inventory, and the offered query forms follow its
 * server-reported capabilities (lib/indexCapabilities.ts holds the fallback for older
 * servers). Index lifecycle and content management live on the Indexes screen.
 */

type QueryMode = "property" | "index";

const VECTOR_KINDS = ["any", "vertex", "edge"] as const;

const OPERATORS = Object.keys(BINARY_OPERATORS) as BinaryOperatorName[];

const RESULT_TYPES = ["Vertices", "Edges", "Both"] as const;

const FORM_LABELS: Record<IndexCapability, string> = {
  equality: "equality / operator",
  range: "range",
  fulltext: "fulltext",
  spatial: "spatial",
  vector: "vector (kNN)",
};

export function QueryScreen() {
  const { instance, store } = useInstanceStore();
  const mergeIntoCanvas = store((s) => s.mergeIntoCanvas);
  const addResultSet = store((s) => s.addResultSet);

  const [mode, setMode] = useState<QueryMode>("property");
  const [propertyId, setPropertyId] = useState("");
  const [indexId, setIndexId] = useState("");
  const [form, setForm] = useState<IndexCapability>("equality");
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
  const provider = useEmbeddingProvider(instance);
  const providerEnabled = provider ? provider.enabled : null;

  // Index picks: the live /status inventory first, shape-snapshot ids as backup (the
  // snapshot may know ids from before a reconnect; the union keeps both honest). Only a
  // server whose /status PREDATES the inventory field gets a free-form input — with a
  // live inventory the dropdown is complete, and a possibly stale shape snapshot must
  // not lock out an index created this session.
  const statusData = useStatus(instance).data;
  const inventory = statusData?.indices ?? [];
  const inventoryKnown = !statusData || statusData.indices != null;
  const indexIdOptions = [
    ...new Set([
      ...inventory.map((i) => i.indexId).filter(Boolean),
      ...suggestions.indexIds,
    ]),
  ];

  // The query forms this index answers. An id the inventory does not know (free-form
  // input, stale shape id) offers every form — a mismatched non-vector form is NOT a
  // server error (those endpoints answer empty), so the result panel hints at it.
  const selectedIndex = inventory.find((i) => i.indexId === indexId);
  const capabilities = indexCapabilities(indexId ? selectedIndex : null);
  useEffect(() => {
    if (!capabilities.includes(form)) setForm(capabilities[0]);
  }, [capabilities, form]);

  // Consume a one-shot prefill (Indexes screen "Query" / Graph shape index row).
  const scanPrefill = store((s) => s.scanPrefill);
  const setScanPrefill = store((s) => s.setScanPrefill);
  useEffect(() => {
    if (scanPrefill) {
      setMode("index");
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
      if (mode === "property") {
        ids =
          (await scanProperty(instance, propertyId, {
            operator: BINARY_OPERATORS[operator],
            literal: toLiteral(literal),
            resultType,
          })) ?? [];
      } else if (form === "equality") {
        ids =
          (await scanIndex(instance, {
            indexId,
            operator: BINARY_OPERATORS[operator],
            literal: toLiteral(literal),
            resultType,
          })) ?? [];
      } else if (form === "range") {
        ids =
          (await scanIndexRange(instance, {
            indexId,
            leftLimit: toLiteral(leftLimit),
            rightLimit: toLiteral(rightLimit),
            includeLeft,
            includeRight,
            resultType,
          })) ?? [];
      } else if (form === "fulltext") {
        const result = await scanFulltext(instance, {
          indexId,
          requestString: fulltextQuery,
        });
        setFulltextResult(result);
        ids = result?.elements.map((e) => e.graphElementId) ?? [];
      } else if (form === "vector") {
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
      addResultSet(
        `${mode === "property" ? "property scan" : `${form} · ${indexId}`} (${ids.length} ids)`,
        ids,
      );
      const hydrated = await hydrateElements(instance, ids, { onProgress: setProgress });
      setCapped(hydrated.capped);
      return hydrated.elements;
    },
    onSuccess: (hydrated) => setElements(hydrated),
    onSettled: () => setProgress(null),
  });

  const needsLiteral = mode === "property" || form === "equality";
  const showResultType = needsLiteral || (mode === "index" && form === "range");
  const parsedVector =
    mode === "index" && form === "vector" ? parseVector(vectorText) : null;
  const vectorNotReady =
    mode === "index" &&
    form === "vector" &&
    (vectorSource === "text"
      ? !vectorSearchText.trim() || providerEnabled !== true
      : !parsedVector?.ok);

  return (
    <div className="mx-auto max-w-5xl space-y-4">
      <section className="panel">
        <div className="panel-title">Query</div>
        <form
          className="space-y-3 p-3"
          onSubmit={(e) => {
            e.preventDefault();
            scan.mutate();
          }}
        >
          <div className="flex flex-wrap items-end gap-3">
            <Field helpKey="scanKind" label="query type" htmlFor="query-mode">
              <select
                id="query-mode"
                data-testid="query-mode"
                className="input w-auto"
                value={mode}
                onChange={(e) => setMode(e.target.value as QueryMode)}
              >
                <option value="property">property scan</option>
                <option value="index">ask an index</option>
              </select>
            </Field>

            {mode === "property" && (
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

            {mode === "index" && (
              <Field helpKey="indexId" label="index" htmlFor="scan-index">
                {inventoryKnown ? (
                  <select
                    id="scan-index"
                    data-testid="index-select"
                    className="input w-44"
                    value={indexId}
                    onChange={(e) => setIndexId(e.target.value)}
                  >
                    <option value="">— pick an index —</option>
                    {indexIdOptions.map((id) => (
                      <option key={id} value={id}>
                        {id}
                      </option>
                    ))}
                  </select>
                ) : (
                  <input
                    id="scan-index"
                    data-testid="index-free"
                    className="input w-44"
                    list="query-index-ids"
                    value={indexId}
                    onChange={(e) => setIndexId(e.target.value)}
                    placeholder="myIndex"
                  />
                )}
              </Field>
            )}
            {mode === "index" && inventoryKnown && indexIdOptions.length === 0 && (
              <span className="text-fg-faint pb-2 text-[11px]" data-testid="no-indexes-note">
                no indexes on this instance — create one on the Indexes screen
              </span>
            )}

            {mode === "index" && indexId && capabilities.length > 1 && (
              <Field helpKey="indexQueryForm" label="query form" htmlFor="query-form">
                <div className="border-line flex overflow-hidden rounded border">
                  {capabilities.map((cap) => (
                    <button
                      key={cap}
                      type="button"
                      data-testid={`form-${cap}`}
                      className={`px-2 py-1 text-[11px] ${
                        form === cap
                          ? "bg-panel-2 text-accent"
                          : "text-fg-dim hover:text-fg"
                      }`}
                      onClick={() => setForm(cap)}
                    >
                      {FORM_LABELS[cap]}
                    </button>
                  ))}
                </div>
              </Field>
            )}
            {mode === "index" && indexId && capabilities.length === 1 && (
              <span className="text-fg-faint pb-2 text-[11px]" data-testid="form-single">
                {FORM_LABELS[capabilities[0]]}
              </span>
            )}

            {(mode === "property" || indexId) && (
              <>
                {needsLiteral && (
                  <>
                    <Field helpKey="scanOperator" label="operator" htmlFor="scan-operator">
                      <select
                        id="scan-operator"
                        className="input w-auto"
                        value={operator}
                        onChange={(e) =>
                          setOperator(e.target.value as BinaryOperatorName)
                        }
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

                {mode === "index" && form === "range" && (
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

                {mode === "index" && form === "fulltext" && (
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

                {mode === "index" && form === "spatial" && (
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

                {mode === "index" && form === "vector" && (
                  <>
                    <div className="flex basis-full items-center gap-2">
                      <span className="text-fg-faint text-[10px] tracking-widest uppercase">
                        query by
                      </span>
                      <div className="border-line flex overflow-hidden rounded border">
                        {(["vector", "text"] as const).map((source) => (
                          <button
                            key={source}
                            type="button"
                            data-testid={`vector-source-${source}`}
                            className={`px-2 py-1 text-[11px] ${
                              vectorSource === source
                                ? "bg-panel-2 text-accent"
                                : "text-fg-dim hover:text-fg"
                            } ${source === "text" && providerEnabled !== true ? "opacity-50" : ""}`}
                            disabled={source === "text" && providerEnabled !== true}
                            title={
                              source === "text" && providerEnabled !== true
                                ? providerEnabled === null
                                  ? "provider status not reported by this server"
                                  : "the embedding provider is off on this instance"
                                : undefined
                            }
                            onClick={() => setVectorSource(source)}
                          >
                            {source === "text" ? "text (provider)" : "vector"}
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
                          embedded once server-side, then kNN — scores identical to a pasted
                          vector
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
                        <div
                          className="text-fg-faint text-[11px]"
                          data-testid="vector-dimension"
                        >
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

                {showResultType && (
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
              </>
            )}

            <button
              type="submit"
              className="btn btn-accent"
              data-testid="scan-run"
              disabled={
                scan.isPending ||
                (mode === "index" && !indexId) ||
                // A blank element id would coerce to Number("") === 0 and silently query
                // the neighborhood of element 0.
                (mode === "index" && form === "spatial" && !spatialElementId.trim()) ||
                vectorNotReady
              }
            >
              {scan.isPending ? "Running…" : "Run query"}
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
          {/* An index the inventory does not know cannot be validated up front, and the
              non-vector scan endpoints answer EMPTY (not an error) for a missing index or
              a form the index does not serve — say so instead of a bare 0. */}
          {idCount === 0 && mode === "index" && indexId && !selectedIndex && (
            <p className="text-warn px-3 pt-3 text-[11px]" data-testid="unknown-index-hint">
              '{indexId}' is not in the live inventory — 0 ids can also mean the index does
              not exist or does not answer this query form.
            </p>
          )}
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

      {/* Suggestions for the old-server free-form index input (shape snapshot / stale ids). */}
      <datalist id="query-index-ids">
        {indexIdOptions.map((id) => (
          <option key={id} value={id} />
        ))}
      </datalist>
      {/* Shared identifier suggestions from the Graph shape snapshot (empty until computed). */}
      <datalist id="shape-property-keys">
        {suggestions.propertyKeys.map((key) => (
          <option key={key} value={key} />
        ))}
      </datalist>
      <datalist id="shape-labels">
        {[...new Set([...suggestions.vertexLabels, ...suggestions.edgeLabels])].map(
          (label) => (
            <option key={label} value={label} />
          ),
        )}
      </datalist>
    </div>
  );
}
