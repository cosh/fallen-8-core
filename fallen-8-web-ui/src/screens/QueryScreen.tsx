import { useEffect, useState } from "react";
import { useMutation } from "@tanstack/react-query";
import { useActiveInstance } from "../instances/registry";
import { shapeSuggestions, useGraphShape } from "../state/graphShape";
import {
  createIndex,
  deleteIndex,
  scanFulltext,
  scanIndex,
  scanIndexRange,
  scanProperty,
  scanSpatial,
} from "../api/endpoints";
import type { EdgeREST, FulltextSearchResultREST, VertexREST } from "../api/types";
import { BINARY_OPERATORS, type BinaryOperatorName } from "../api/types";
import { toLiteral, type TypedValue } from "../lib/literals";
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

type ScanKind = "property" | "index" | "range" | "fulltext" | "spatial";

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

  const [progress, setProgress] = useState<HydrationProgress | null>(null);
  const [elements, setElements] = useState<(VertexREST | EdgeREST)[]>([]);
  const [fulltextResult, setFulltextResult] = useState<FulltextSearchResultREST | null>(null);
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
              disabled={scan.isPending}
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
          <ElementTable elements={elements} />
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
    </div>
  );
}

function IndexManagement() {
  const instance = useActiveInstance()!;
  const [indexId, setIndexId] = useState("");
  const [indexType, setIndexType] = useState("DictionaryIndex");
  const [message, setMessage] = useState<string | null>(null);

  const create = useMutation({
    mutationFn: () =>
      createIndex(instance, { uniqueId: indexId, pluginType: indexType }),
    onSuccess: () => setMessage(`Index '${indexId}' created.`),
  });
  const remove = useMutation({
    mutationFn: () => deleteIndex(instance, indexId),
    onSuccess: () => setMessage(`Index '${indexId}' deleted.`),
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
      {(create.isError || remove.isError) && (
        <div className="px-3 pb-3">
          <ErrorBox error={create.error ?? remove.error} />
        </div>
      )}
      <p className="text-fg-faint px-3 pb-3 text-[11px]">
        Ids here are free-form; compute the Graph shape snapshot on the Analytics screen
        to feed property/index suggestions into these inputs (gap G-3).
      </p>
    </section>
  );
}
