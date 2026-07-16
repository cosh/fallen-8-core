import { useEffect, useState } from "react";
import { useMutation } from "@tanstack/react-query";
import { useActiveInstance } from "../instances/registry";
import { shapeSuggestions, useGraphShape } from "../state/graphShape";
import {
  addVectorToIndex,
  createIndex,
  deleteIndex,
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
import { ElementTable } from "../components/ElementTable";
import { ErrorBox } from "../components/ErrorBox";
import { getInstanceStore } from "../state/instanceStore";

/**
 * Query workspace (FR-8/9/10/11): the scan types with typed literals, id-list
 * hydration with progress, open-as-table + send-to-canvas, and index management.
 * Identifier inputs are free-form with <datalist> suggestions fed by the Graph shape
 * snapshot (feature studio-coverage — closes gap G-3 when a snapshot exists).
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

  const [progress, setProgress] = useState<HydrationProgress | null>(null);
  const [elements, setElements] = useState<(VertexREST | EdgeREST)[]>([]);
  const [fulltextResult, setFulltextResult] = useState<FulltextSearchResultREST | null>(null);
  const [vectorResult, setVectorResult] = useState<VectorSearchResultREST | null>(null);
  const [idCount, setIdCount] = useState<number | null>(null);
  const [capped, setCapped] = useState(false);

  const suggestions = shapeSuggestions(useGraphShape(instance).data);

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
        const parsed = parseVector(vectorText);
        if (!parsed.ok) {
          throw new Error(`Query vector: ${parsed.error}.`);
        }
        const result = await scanVector(instance, {
          indexId,
          query: parsed.vector,
          k: Number(vectorK),
          kind: vectorKind === "any" ? undefined : vectorKind,
          label: vectorLabel || undefined,
        });
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
            <div>
              <label className="label" htmlFor="scan-kind">
                scan type
              </label>
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
            </div>

            {kind === "property" && (
              <div>
                <label className="label" htmlFor="scan-property">
                  property id
                </label>
                <input
                  id="scan-property"
                  data-testid="scan-property"
                  className="input w-40"
                  list="shape-property-keys"
                  value={propertyId}
                  onChange={(e) => setPropertyId(e.target.value)}
                  placeholder="age"
                />
              </div>
            )}

            {needsIndex && (
              <div>
                <label className="label" htmlFor="scan-index">
                  index id
                </label>
                <input
                  id="scan-index"
                  className="input w-40"
                  list="shape-index-ids"
                  value={indexId}
                  onChange={(e) => setIndexId(e.target.value)}
                  placeholder="myIndex"
                />
              </div>
            )}

            {needsLiteral && (
              <>
                <div>
                  <label className="label" htmlFor="scan-operator">
                    operator
                  </label>
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
                </div>
                <TypedLiteralEditor
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
                  label="left limit"
                  idPrefix="range-left"
                  value={leftLimit}
                  onChange={setLeftLimit}
                />
                <TypedLiteralEditor
                  label="right limit"
                  idPrefix="range-right"
                  value={rightLimit}
                  onChange={setRightLimit}
                />
                <label className="text-fg-dim flex items-center gap-1 text-[12px]">
                  <input
                    type="checkbox"
                    checked={includeLeft}
                    onChange={(e) => setIncludeLeft(e.target.checked)}
                  />
                  incl. left
                </label>
                <label className="text-fg-dim flex items-center gap-1 text-[12px]">
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
              <div className="grow">
                <label className="label" htmlFor="fulltext-query">
                  query
                </label>
                <input
                  id="fulltext-query"
                  className="input"
                  value={fulltextQuery}
                  onChange={(e) => setFulltextQuery(e.target.value)}
                  placeholder="search text"
                />
              </div>
            )}

            {kind === "spatial" && (
              <>
                <div>
                  <label className="label" htmlFor="spatial-element">
                    element id
                  </label>
                  <input
                    id="spatial-element"
                    className="input w-28"
                    value={spatialElementId}
                    onChange={(e) => setSpatialElementId(e.target.value)}
                  />
                </div>
                <div>
                  <label className="label" htmlFor="spatial-distance">
                    distance
                  </label>
                  <input
                    id="spatial-distance"
                    className="input w-28"
                    value={spatialDistance}
                    onChange={(e) => setSpatialDistance(e.target.value)}
                  />
                </div>
              </>
            )}

            {kind === "vector" && (
              <>
                <div className="grow basis-full">
                  <label className="label" htmlFor="vector-query">
                    query vector (JSON array or comma-separated floats)
                  </label>
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
                </div>
                <div>
                  <label className="label" htmlFor="vector-k">
                    k (1–1024)
                  </label>
                  <input
                    id="vector-k"
                    className="input w-20"
                    type="number"
                    min={1}
                    max={1024}
                    value={vectorK}
                    onChange={(e) => setVectorK(e.target.value)}
                  />
                </div>
                <div>
                  <label className="label" htmlFor="vector-kind">
                    element kind
                  </label>
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
                </div>
                <div>
                  <label className="label" htmlFor="vector-label">
                    label constraint
                  </label>
                  <input
                    id="vector-label"
                    className="input w-32"
                    list="shape-labels"
                    value={vectorLabel}
                    onChange={(e) => setVectorLabel(e.target.value)}
                    placeholder="person"
                  />
                </div>
                <p className="text-fg-faint basis-full text-[11px]">
                  constraints apply before top-k — you get k matching elements.
                </p>
              </>
            )}

            {needsLiteral && (
              <div>
                <label className="label" htmlFor="scan-result-type">
                  result type
                </label>
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
              </div>
            )}

            <button
              type="submit"
              className="btn btn-accent"
              data-testid="scan-run"
              disabled={scan.isPending || (kind === "vector" && !parsedVector?.ok)}
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
        {suggestions.indexIds.map((id) => (
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
    </div>
  );
}

function IndexManagement() {
  const instance = useActiveInstance()!;
  const [indexId, setIndexId] = useState("");
  const [indexType, setIndexType] = useState("DictionaryIndex");
  const [message, setMessage] = useState<string | null>(null);
  const [dimension, setDimension] = useState("384");
  const [metric, setMetric] = useState("Cosine");
  const [showVectorAdd, setShowVectorAdd] = useState(false);
  const [vaElementId, setVaElementId] = useState("");
  const [vaMode, setVaMode] = useState<"property" | "explicit">("property");
  const [vaPropertyId, setVaPropertyId] = useState("embedding");
  const [vaVectorText, setVaVectorText] = useState("");

  const isVectorIndex = indexType.trim() === "VectorIndex";

  const create = useMutation({
    mutationFn: () =>
      createIndex(instance, {
        uniqueId: indexId,
        pluginType: indexType,
        // VectorIndex options travel as typed literals (vector-index README §creation).
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
            }
          : undefined,
      }),
    onSuccess: () => setMessage(`Index '${indexId}' created.`),
  });
  const remove = useMutation({
    mutationFn: () => deleteIndex(instance, indexId),
    onSuccess: () => setMessage(`Index '${indexId}' deleted.`),
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
      <div className="flex flex-wrap items-end gap-2 p-3">
        <div>
          <label className="label" htmlFor="index-id">
            index id
          </label>
          <input
            id="index-id"
            className="input w-40"
            list="shape-index-ids"
            value={indexId}
            onChange={(e) => setIndexId(e.target.value)}
            placeholder="myIndex"
          />
        </div>
        <div>
          <label className="label" htmlFor="index-type">
            plugin type
          </label>
          <input
            id="index-type"
            className="input w-48"
            value={indexType}
            onChange={(e) => setIndexType(e.target.value)}
            placeholder="DictionaryIndex"
          />
        </div>
        {isVectorIndex && (
          <>
            <div>
              <label className="label" htmlFor="vector-dimension-opt">
                dimension (1–4096)
              </label>
              <input
                id="vector-dimension-opt"
                className="input w-24"
                type="number"
                min={1}
                max={4096}
                value={dimension}
                onChange={(e) => setDimension(e.target.value)}
              />
            </div>
            <div>
              <label className="label" htmlFor="vector-metric">
                metric
              </label>
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
            </div>
          </>
        )}
        <button
          type="button"
          className="btn btn-accent"
          disabled={!indexId.trim() || create.isPending}
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
            <div>
              <label className="label" htmlFor="va-element">
                element id
              </label>
              <input
                id="va-element"
                className="input w-28"
                value={vaElementId}
                onChange={(e) => setVaElementId(e.target.value)}
              />
            </div>
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
              <div>
                <label className="label" htmlFor="va-property">
                  property id
                </label>
                <input
                  id="va-property"
                  className="input w-32"
                  list="shape-property-keys"
                  value={vaPropertyId}
                  onChange={(e) => setVaPropertyId(e.target.value)}
                  placeholder="embedding"
                />
              </div>
            ) : (
              <div className="grow">
                <label className="label" htmlFor="va-vector">
                  vector
                </label>
                <input
                  id="va-vector"
                  className="input w-full font-mono"
                  value={vaVectorText}
                  onChange={(e) => setVaVectorText(e.target.value)}
                  placeholder="[0.12, -0.5, 0.33]"
                />
              </div>
            )}
            <button
              type="button"
              className="btn btn-accent"
              disabled={
                !indexId.trim() ||
                !vaElementId.trim() ||
                (vaMode === "property" ? !vaPropertyId.trim() : !vaVectorText.trim()) ||
                vectorAdd.isPending
              }
              onClick={() => vectorAdd.mutate()}
            >
              Add vector
            </button>
            <p className="text-fg-faint basis-full text-[11px]">
              targets the index id above · property mode is WAL-recoverable — the honest
              default; bulk embedding loads belong to your pipeline, not a browser.
            </p>
          </div>
        )}
      </div>
      {(create.isError || remove.isError || vectorAdd.isError) && (
        <div className="px-3 pb-3">
          <ErrorBox error={create.error ?? remove.error ?? vectorAdd.error} />
        </div>
      )}
      <p className="text-fg-faint px-3 pb-3 text-[11px]">
        Ids here are free-form; compute the Graph shape snapshot on the Analytics screen
        to feed property/index suggestions into these inputs (gap G-3).
      </p>
    </section>
  );
}
