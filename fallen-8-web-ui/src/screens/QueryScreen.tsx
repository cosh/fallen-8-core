// MIT License
//
// QueryScreen.tsx
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

import { useEffect, useState } from "react";
import { useMutation, useQueryClient } from "@tanstack/react-query";
import { useInstanceStore } from "../instances/registry";
import { shapeSuggestions, useEmbeddingProvider, useGraphShape } from "../state/graphShape";
import { useStatus } from "../state/status";
import { QUERY_MODES, type QueryDraft, type QueryMode } from "../state/instanceStore";
import {
  createIndex,
  embeddingSearch,
  scanFulltext,
  scanIndex,
  scanIndexRange,
  scanProperties,
  scanProperty,
  scanSpatial,
  scanVector,
} from "../api/endpoints";
import type {
  EdgeREST,
  EmbeddingProviderStatsREST,
  FulltextSearchResultREST,
  VectorSearchResultREST,
  VertexREST,
} from "../api/types";
import { BINARY_OPERATORS, type BinaryOperatorName } from "../api/types";
import { toLiteral } from "../lib/literals";
import { parseVector } from "../lib/vector";
import { embeddingStamp } from "../lib/modelProvenance";
import {
  indexCapabilities,
  type IndexCapability,
} from "../lib/indexCapabilities";
import {
  isValidVectorDimension,
  vectorIndexDefaults,
  vectorIndexPluginOptions,
} from "../lib/vectorIndexCreate";
import { hydrateElements, isEdge, type HydrationProgress } from "../lib/hydrate";
import { TypedLiteralEditor } from "../components/TypedLiteralEditor";
import { Field } from "../components/Field";
import { help } from "../lib/fieldHelp";
import { ElementTable } from "../components/ElementTable";
import { ErrorBox } from "../components/ErrorBox";
/**
 * Query workspace (FR-8/9/11, re-shaped by feature index-workspace): a property scan (the
 * index-less path), an index query, or a semantic search. Index queries are INDEX-FIRST: pick
 * the index from the live /status inventory, and the offered query forms follow its
 * server-reported capabilities (lib/indexCapabilities.ts holds the fallback for older
 * servers). Index lifecycle and content management live on the Indexes screen.
 *
 * The semantic mode (feature semantic-search-onramp) is text-in kNN. It was a source toggle
 * inside the index mode's vector form, which made the one capability an operator can use
 * without knowing the data model reachable only by first knowing the index model; it is its
 * own mode for that reason, and when the instance has no vector index at all it offers the
 * one create call that fixes that rather than an empty picker.
 */

const MODE_LABELS: Record<QueryMode, string> = {
  property: "property scan",
  index: "ask an index",
  semantic: "semantic search",
};

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

  // The whole input form lives in the persisted per-instance store, so leaving for the
  // Canvas and returning restores it exactly (feature index-workspace / studio state
  // persistence). Results are re-run on demand and stay local (below).
  const draft = store((s) => s.queryDraft);
  const setQueryDraft = store((s) => s.setQueryDraft);
  const resetQueryDraft = store((s) => s.resetQueryDraft);
  const {
    mode,
    propertyScope,
    propertyId,
    searchTerm,
    searchLabel,
    indexId,
    semanticIndexId,
    form,
    operator,
    resultType,
    literal,
    leftLimit,
    rightLimit,
    includeLeft,
    includeRight,
    fulltextQuery,
    spatialElementId,
    spatialDistance,
    vectorText,
    vectorK,
    vectorKind,
    vectorLabel,
    vectorSearchText,
  } = draft;

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
    if (!capabilities.includes(form)) setQueryDraft({ form: capabilities[0] });
  }, [capabilities, form, setQueryDraft]);

  const semantic = mode === "semantic";
  const vectorForm = mode === "index" && form === "vector";
  // A semantic search ranks against a vector index and nothing else, so the mode offers only
  // those. The capability form (not pluginType) is the one home for what an index answers,
  // including the fallback for servers that report none.
  const vectorIndexes = inventory.filter((i) => indexCapabilities(i).includes("vector"));
  // The pick this mode will actually use. It has its own draft field rather than sharing
  // `indexId`, because the two modes choose from different sets and one field meant a semantic
  // pick silently replaced the index mode's selection AND the query form that went with it.
  // Still normalized rather than trusted: a stored id that no longer names a vector index
  // (deleted meanwhile, or carried from another instance) reads as "nothing picked" instead of
  // being sent to be rejected, and a single vector index needs no picking at all. An old server
  // that cannot enumerate its inventory is not second-guessed: whatever was typed stands.
  const semanticPick = !inventoryKnown
    ? semanticIndexId
    : vectorIndexes.some((i) => i.indexId === semanticIndexId)
      ? semanticIndexId
      : vectorIndexes.length === 1
        ? vectorIndexes[0].indexId
        : "";
  // The index this run actually queries, so the honesty checks below hold for both kNN sources.
  const activeIndexId = semantic ? semanticPick : indexId;
  const activeIndex = inventory.find((i) => i.indexId === activeIndexId);
  // Embeddings are element state, so they can already sit on thousands of elements while the
  // projection that ranks them does not exist yet. That state is one create call away from a
  // working search, and showing an empty picker instead is the dead end this mode removes.
  //
  // `inventoryKnown` is the OLD-SERVER question (does /status carry the field at all) and is
  // deliberately true before the request lands, so the picker does not flash a free-form input.
  // Claiming "this instance has none" needs strictly more than that: a request still in flight,
  // or one that failed against an unreachable or unauthorized instance, knows nothing about what
  // exists. Offering to CREATE on the strength of a pending request is the same false certainty
  // this mode was built to remove, one level down.
  const inventoryArrived = statusData != null;
  // "The provider is off" is a fact carried by a provider block. "This server does not report
  // one" only becomes a fact once a /status has come back WITHOUT one; asserting it against a
  // request still in flight, or one that failed because the instance is unreachable, describes
  // the network as a configuration choice.
  const providerVerdictKnown = providerEnabled === false || inventoryArrived;
  const semanticOnRamp =
    semantic && inventoryArrived && inventoryKnown && vectorIndexes.length === 0;
  // The picker is the only thing the on-ramp replaces. The question itself (text, k, kind,
  // label) stays on screen either way, so an operator can write what they are looking for and
  // then make the index that will answer it, rather than being sent away and back.
  const semanticPicker = semantic && !semanticOnRamp;

  // Consume a one-shot prefill (Indexes screen "Query" / Graph shape index row).
  const scanPrefill = store((s) => s.scanPrefill);
  const setScanPrefill = store((s) => s.setScanPrefill);
  // The element a "find similar" gesture started from, dropped from its own results. There is no
  // self-exclusion in the engine, the REST contract or the MCP bridge, so an unfiltered similarity
  // search returns the source element at rank 1 every time. Visible and clearable rather than
  // hidden, because a silently filtered result set is one nobody can reason about.
  const [excludeElementId, setExcludeElementId] = useState<number | null>(null);
  // The engine's own ceiling. The over-fetch has to respect it or a find-similar search at the
  // advertised maximum k answers 400 instead of dropping one hit - the same clamp the MCP bridge
  // already applies to this trick.
  const MAX_K = 1024;
  // The exclusion belongs to the find-similar QUESTION, "elements like this element's vector",
  // and that question lives only in the vector form the prefill lands in. Allowed to follow the
  // operator into the semantic mode, it dropped a hit from a TEXT search, spent one of their k
  // on the over-fetch, and explained itself with a chip about a vector that query never had. It
  // is not cleared on the way out, so returning to the vector form finds it intact.
  const exclusionActive = excludeElementId !== null && vectorForm;
  const fetchK = exclusionActive ? Math.min(Number(vectorK) + 1, MAX_K) : Number(vectorK);
  useEffect(() => {
    if (scanPrefill) {
      // A prefill always carries a VECTOR (find similar reads the element's own embedding), so
      // it lands in the index mode's vector form rather than the semantic mode: there is
      // nothing to type, and re-embedding a vector as words would be a different question.
      setQueryDraft({
        mode: "index",
        indexId: scanPrefill.indexId,
        ...(scanPrefill.vectorText !== undefined
          ? {
              form: "vector" as const,
              vectorText: scanPrefill.vectorText,
              vectorLabel: scanPrefill.label ?? "",
              vectorKind: scanPrefill.kind ?? "any",
            }
          : {}),
      });
      setExcludeElementId(scanPrefill.sourceElementId ?? null);
      setScanPrefill(null);
    }
  }, [scanPrefill, setScanPrefill, setQueryDraft]);

  // Results are ephemeral (kept local, never persisted): the lean per-instance store holds
  // the input draft only, so returning from the Canvas restores the form and re-runs give
  // the results back. Clear resets both.
  const clearResults = () => {
    setElements([]);
    setFulltextResult(null);
    setVectorResult(null);
    setIdCount(null);
    setCapped(false);
    setProgress(null);
    // The exclusion belongs to the find-similar question, not to the form. Leaving it set would
    // keep filtering an element out of a query that has nothing to do with it.
    setExcludeElementId(null);
  };

  const scan = useMutation({
    mutationFn: async () => {
      setElements([]);
      setFulltextResult(null);
      setVectorResult(null);
      setIdCount(null);
      setCapped(false);
      setProgress(null);

      let ids: number[] = [];
      if (mode === "property" && propertyScope === "any") {
        // All-property discovery: a case-insensitive contains across every property value.
        ids =
          (await scanProperties(instance, {
            searchTerm,
            label: searchLabel || undefined,
            resultType,
          })) ?? [];
      } else if (mode === "property") {
        ids =
          (await scanProperty(instance, propertyId, {
            operator: BINARY_OPERATORS[operator],
            literal: toLiteral(literal),
            resultType,
          })) ?? [];
      } else if (semantic || form === "vector") {
        // ONE kNN surface, two query sources: the semantic mode sends the sentence and lets the
        // provider embed it once server-side, the vector form sends a vector somebody already
        // has. Scores and ordering are identical for the same vector, so the result rendering
        // below is shared too.
        const knn = {
          k: fetchK,
          kind: vectorKind === "any" ? undefined : vectorKind,
          label: vectorLabel || undefined,
        };
        let result: VectorSearchResultREST | null;
        if (semantic) {
          result = await embeddingSearch(instance, {
            indexId: semanticPick,
            text: vectorSearchText,
            ...knn,
          });
        } else {
          const parsed = parseVector(vectorText);
          if (!parsed.ok) {
            throw new Error(`Query vector: ${parsed.error}.`);
          }
          result = await scanVector(instance, { indexId, query: parsed.vector, ...knn });
        }
        if (exclusionActive && result?.results) {
          // k+1 was requested, so dropping the source still leaves k hits when k of them exist.
          result = {
            ...result,
            results: result.results
              .filter((r) => r.graphElementId !== excludeElementId)
              .slice(0, Number(vectorK)),
          };
        }
        setVectorResult(result);
        ids = result?.results?.map((r) => r.graphElementId) ?? [];
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
      } else {
        ids =
          (await scanSpatial(instance, {
            indexId,
            graphElementId: Number(spatialElementId),
            distance: Number(spatialDistance),
          })) ?? [];
      }

      setIdCount(ids.length);
      const resultLabel = semantic
        ? `semantic · ${semanticPick}`
        : mode === "index"
          ? `${form} · ${indexId}`
          : propertyScope === "any"
            ? `all-property "${searchTerm}"`
            : "property scan";
      addResultSet(`${resultLabel} (${ids.length} ids)`, ids);
      const hydrated = await hydrateElements(instance, ids, { onProgress: setProgress });
      setCapped(hydrated.capped);
      return hydrated.elements;
    },
    onSuccess: (hydrated) => setElements(hydrated),
    onSettled: () => setProgress(null),
  });

  // The named-key property scan and the index equality form take an operator + typed literal;
  // the all-property scope takes a plain search term instead (feature all-property-search).
  const allProperty = mode === "property" && propertyScope === "any";
  const needsLiteral =
    (mode === "property" && propertyScope === "key") || (mode === "index" && form === "equality");
  const showResultType =
    needsLiteral || allProperty || (mode === "index" && form === "range");
  const parsedVector = vectorForm ? parseVector(vectorText) : null;
  // An emptied k box coerces to Number("") === 0 and the engine answers 400. The input's own
  // min/max stop a typed 5000 but never an empty value, and with an exclusion active it is worse
  // than a 400: fetchK becomes 1 and the slice becomes slice(0, 0), so a good answer renders as
  // no hits. Checked here, with the reason shown beside the field and not only in the button.
  const kValid = /^\d+$/.test(vectorK.trim()) && Number(vectorK) >= 1 && Number(vectorK) <= MAX_K;
  const knnNotReady = semantic
    ? // The server would answer 403 without a provider and 404 without an index; neither is a
      // request worth making, and the disabled Run button says so beside its own reason below.
      !vectorSearchText.trim() || providerEnabled !== true || !semanticPick || !kValid
    : vectorForm
      ? !parsedVector?.ok || !kValid
      : false;
  // An empty vector index is indistinguishable from "nothing is similar" at the search surface: kNN
  // over a zero-length scan succeeds, so both handlers answer 200 with an empty list. The member
  // count is already on the inventory row, one screen away from where the confusion happens.
  const emptyVectorIndex =
    (semantic || vectorForm) && activeIndex != null && activeIndex.values === 0;

  return (
    <div className="mx-auto max-w-5xl space-y-4">
      <section className="panel">
        <div className="panel-title">
          Query
          <button
            type="button"
            className="btn ml-auto"
            data-testid="query-clear"
            title="Reset every query input to its default"
            onClick={() => {
              resetQueryDraft();
              clearResults();
            }}
          >
            Clear
          </button>
        </div>
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
                onChange={(e) => setQueryDraft({ mode: e.target.value as QueryMode })}
              >
                {QUERY_MODES.map((m) => (
                  <option key={m} value={m}>
                    {MODE_LABELS[m]}
                  </option>
                ))}
              </select>
            </Field>

            {mode === "property" && (
              <Field helpKey="propertyScope" label="scope" htmlFor="property-scope">
                <div className="border-line flex overflow-hidden rounded border">
                  {(["key", "any"] as const).map((scope) => (
                    <button
                      key={scope}
                      type="button"
                      data-testid={`property-scope-${scope}`}
                      className={`px-2 py-1 text-[11px] ${
                        propertyScope === scope
                          ? "bg-panel-2 text-accent"
                          : "text-fg-dim hover:text-fg"
                      }`}
                      onClick={() => setQueryDraft({ propertyScope: scope })}
                    >
                      {scope === "key" ? "specific key" : "any property"}
                    </button>
                  ))}
                </div>
              </Field>
            )}

            {mode === "property" && propertyScope === "key" && (
              <Field helpKey="propertyId" label="property id" htmlFor="scan-property">
                <input
                  id="scan-property"
                  data-testid="scan-property"
                  className="input w-40"
                  list="shape-property-keys"
                  value={propertyId}
                  onChange={(e) => setQueryDraft({ propertyId: e.target.value })}
                  placeholder="age"
                />
              </Field>
            )}

            {allProperty && (
              <>
                <Field helpKey="searchTerm" label="search term" htmlFor="scan-search-term">
                  <input
                    id="scan-search-term"
                    data-testid="scan-search-term"
                    className="input w-56"
                    value={searchTerm}
                    onChange={(e) => setQueryDraft({ searchTerm: e.target.value })}
                    placeholder="acme"
                  />
                </Field>
                <Field
                  helpKey="searchLabel"
                  label="label (optional)"
                  htmlFor="scan-search-label"
                >
                  <input
                    id="scan-search-label"
                    data-testid="scan-search-label"
                    className="input w-32"
                    list="shape-labels"
                    value={searchLabel}
                    onChange={(e) => setQueryDraft({ searchLabel: e.target.value })}
                    placeholder="any label"
                  />
                </Field>
              </>
            )}

            {mode === "index" && (
              <Field helpKey="indexId" label="index" htmlFor="scan-index">
                {inventoryKnown ? (
                  <select
                    id="scan-index"
                    data-testid="index-select"
                    className="input w-44"
                    value={indexId}
                    onChange={(e) => setQueryDraft({ indexId: e.target.value })}
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
                    onChange={(e) => setQueryDraft({ indexId: e.target.value })}
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
                      onClick={() => setQueryDraft({ form: cap })}
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

            {semanticOnRamp && (
              <SemanticOnRamp
                provider={provider}
                onCreated={(created) => setQueryDraft({ semanticIndexId: created })}
              />
            )}

            {semantic && (
              <>
                {semanticPicker && (
                  <Field
                    helpKey="semanticIndexId"
                    label="vector index"
                    htmlFor="semantic-index"
                  >
                    {inventoryKnown ? (
                      <select
                        id="semantic-index"
                        data-testid="semantic-index-select"
                        className="input w-44"
                        value={semanticPick}
                        onChange={(e) => setQueryDraft({ semanticIndexId: e.target.value })}
                      >
                        {/* Suppressed only when there is no choice to make: a single vector
                            index is preselected, so a placeholder would be a way to un-pick the
                            only answer. Kept for the empty case, where the alternative is a
                            blank control. */}
                        {vectorIndexes.length !== 1 && (
                          <option value="">— pick a vector index —</option>
                        )}
                        {vectorIndexes.map((i) => (
                          <option key={i.indexId} value={i.indexId}>
                            {i.embeddingName
                              ? `${i.indexId} (bound:${i.embeddingName})`
                              : i.indexId}
                          </option>
                        ))}
                      </select>
                    ) : (
                      <input
                        id="semantic-index"
                        data-testid="semantic-index-free"
                        className="input w-44"
                        list="query-index-ids"
                        value={semanticIndexId}
                        onChange={(e) => setQueryDraft({ semanticIndexId: e.target.value })}
                        placeholder="embeddings"
                      />
                    )}
                  </Field>
                )}
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
                    onChange={(e) => setQueryDraft({ vectorSearchText: e.target.value })}
                    placeholder="red bicycles"
                    disabled={providerEnabled !== true}
                  />
                  <div
                    className="text-fg-faint text-[11px]"
                    data-testid="vector-search-provenance"
                  >
                    embedded once server-side, then kNN — scores identical to a pasted vector
                    {/* Gated on the RESOLVED state, like SemanticQueryEditor's twin: naming a
                        backend for a request that cannot be made is exactly the wrong claim. */}
                    {providerEnabled === true &&
                      provider &&
                      ` · via ${provider.backend ?? "?"} · ${embeddingStamp(provider)}`}
                  </div>
                </Field>
                <KnnParamFields
                  vectorK={vectorK}
                  kValid={kValid}
                  vectorKind={vectorKind}
                  vectorLabel={vectorLabel}
                  setQueryDraft={setQueryDraft}
                />
                {!inventoryArrived && (
                  <p
                    className="text-fg-faint basis-full text-[11px]"
                    data-testid="semantic-inventory-pending"
                  >
                    This instance has not answered with its index inventory yet, so there is
                    nothing to pick from and nothing to search. If it stays this way, the
                    connection state in the header is the thing to look at.
                  </p>
                )}
                {providerEnabled !== true && providerVerdictKnown && (
                  <p
                    className="text-warn basis-full text-[11px]"
                    data-testid="semantic-provider-off"
                  >
                    {providerEnabled === null
                      ? "This server does not report an embedding provider, so it cannot turn typed words into a vector."
                      : "The embedding provider is off on this instance, so it cannot turn typed words into a vector."}{" "}
                    A vector you already have still works: switch to 'ask an index' and pick the
                    vector (kNN) form.
                  </p>
                )}
              </>
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
                          setQueryDraft({ operator: e.target.value as BinaryOperatorName })
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
                      onChange={(v) => setQueryDraft({ literal: v })}
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
                      onChange={(v) => setQueryDraft({ leftLimit: v })}
                    />
                    <TypedLiteralEditor
                      helpKey="rangeRightLimit"
                      label="right limit"
                      idPrefix="range-right"
                      value={rightLimit}
                      onChange={(v) => setQueryDraft({ rightLimit: v })}
                    />
                    <label
                      className="text-fg-dim label-help flex items-center gap-1 text-[12px]"
                      title={help("rangeIncludeLeft")}
                    >
                      <input
                        type="checkbox"
                        checked={includeLeft}
                        onChange={(e) => setQueryDraft({ includeLeft: e.target.checked })}
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
                        onChange={(e) => setQueryDraft({ includeRight: e.target.checked })}
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
                      onChange={(e) => setQueryDraft({ fulltextQuery: e.target.value })}
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
                        onChange={(e) => setQueryDraft({ spatialElementId: e.target.value })}
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
                        onChange={(e) => setQueryDraft({ spatialDistance: e.target.value })}
                      />
                    </Field>
                  </>
                )}

                {vectorForm && (
                  <>
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
                        onChange={(e) => setQueryDraft({ vectorText: e.target.value })}
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
                    <KnnParamFields
                      vectorK={vectorK}
                      kValid={kValid}
                      vectorKind={vectorKind}
                      vectorLabel={vectorLabel}
                      setQueryDraft={setQueryDraft}
                    />
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
                        setQueryDraft({
                          resultType: e.target.value as (typeof RESULT_TYPES)[number],
                        })
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
                // A blank all-property term must not round-trip (it is a 400 server-side).
                (allProperty && !searchTerm.trim()) ||
                // A blank element id would coerce to Number("") === 0 and silently query
                // the neighborhood of element 0.
                (mode === "index" && form === "spatial" && !spatialElementId.trim()) ||
                knnNotReady
              }
            >
              {scan.isPending ? "Running…" : "Run query"}
            </button>
            {exclusionActive && (
              <span className="text-fg-faint text-[11px]" data-testid="exclude-source-chip">
                excluding #{excludeElementId}, the element this vector came from
                <button
                  type="button"
                  className="btn ml-1"
                  data-testid="exclude-source-clear"
                  onClick={() => setExcludeElementId(null)}
                >
                  include it
                </button>
              </span>
            )}
            {emptyVectorIndex && (
              <span className="text-warn text-[11px]" data-testid="empty-vector-index-hint">
                '{activeIndexId}' has no members yet, so this can only answer 0 hits
                {activeIndex?.embeddingName
                  ? ` — it is bound to the '${activeIndex.embeddingName}' embedding, so write that embedding on some elements, check the name, or check its dimension: a bound index holds none of the vectors whose length disagrees with it`
                  : " — add vectors to it, or bind it to an element embedding when you create it"}
                .
              </span>
            )}
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
              Send all to canvas
            </button>
          </div>
          {fulltextResult && (
            <div className="border-line border-b p-3 text-[12px]">
              <div className="text-fg-faint mb-1 text-[10px] tracking-widest uppercase">
                highlights (max score {fulltextResult.maximumScore.toFixed(2)})
              </div>
              {fulltextResult.elements.slice(0, 20).map((el) => (
                <div key={el.graphElementId} className="text-fg-dim line-clamp-2 wrap-break-word">
                  #{el.graphElementId} ({el.score.toFixed(2)}): {el.highlights.join(" … ")}
                </div>
              ))}
            </div>
          )}
          <ElementTable
            elements={elements}
            onAddToCanvas={(el) =>
              isEdge(el) ? mergeIntoCanvas([], [el]) : mergeIntoCanvas([el], [])
            }
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
      {/* Embedding names seen on the graph, for the on-ramp's binding input. */}
      <datalist id="shape-embedding-names">
        {suggestions.embeddingNames.map((name) => (
          <option key={name} value={name} />
        ))}
      </datalist>
    </div>
  );
}

/**
 * The kNN parameters both query sources share: the semantic mode and the index mode's vector
 * form ask the same question of the same index and differ only in how the query vector arrives,
 * so k, kind and label have one home instead of two copies that drift.
 */
function KnnParamFields({
  vectorK,
  kValid,
  vectorKind,
  vectorLabel,
  setQueryDraft,
}: {
  vectorK: string;
  kValid: boolean;
  vectorKind: QueryDraft["vectorKind"];
  vectorLabel: string;
  setQueryDraft: (patch: Partial<QueryDraft>) => void;
}) {
  return (
    <>
      <Field helpKey="vectorK" label="k (1–1024)" htmlFor="vector-k">
        <input
          id="vector-k"
          className="input w-20"
          type="number"
          min={1}
          max={1024}
          value={vectorK}
          onChange={(e) => setQueryDraft({ vectorK: e.target.value })}
        />
        {!kValid && (
          <div className="text-warn text-[11px]" data-testid="k-invalid">
            a whole number from 1 to 1024
          </div>
        )}
      </Field>
      <Field helpKey="vectorKind" label="element kind" htmlFor="vector-kind">
        <select
          id="vector-kind"
          className="input w-auto"
          value={vectorKind}
          onChange={(e) =>
            setQueryDraft({
              vectorKind: e.target.value as (typeof VECTOR_KINDS)[number],
            })
          }
        >
          {VECTOR_KINDS.map((k) => (
            <option key={k}>{k}</option>
          ))}
        </select>
      </Field>
      <Field helpKey="vectorLabelConstraint" label="label constraint" htmlFor="vector-label">
        <input
          id="vector-label"
          className="input w-32"
          list="shape-labels"
          value={vectorLabel}
          onChange={(e) => setQueryDraft({ vectorLabel: e.target.value })}
          placeholder="person"
        />
      </Field>
    </>
  );
}

/**
 * The on-ramp: what the semantic mode offers when the instance has NO vector index (feature
 * semantic-search-onramp). A bound vector index is a self-maintaining projection that
 * materialises itself over the embeddings already on the elements, so the distance from
 * "12,000 embedded elements, nothing to search with" to a working search is one create call.
 * Offered only with the provider ON: without it the search this index exists for answers 403,
 * and building an index nobody can query here would be a worse dead end than the one it fixes.
 *
 * It names the index it created rather than leaving the mode's single-index preselect to find it,
 * because that preselect only holds while exactly one vector index exists: a second one arriving
 * between the create and the refetch (another session, another operator) would otherwise leave
 * the picker asking the user to choose the thing they just made.
 */
function SemanticOnRamp({
  provider,
  onCreated,
}: {
  provider: EmbeddingProviderStatsREST | null;
  onCreated: (indexId: string) => void;
}) {
  const { instance } = useInstanceStore();
  const queryClient = useQueryClient();
  const [indexId, setIndexId] = useState("embeddings");
  const [embeddingName, setEmbeddingName] = useState("default");
  // NULL means "nobody has typed here yet", so the provider's own numbers stay the default
  // without an effect that would clobber a typed value when the status request lands. Same
  // reasoning, and the same shared derivation, as the Indexes screen's create panel.
  const [dimensionEdit, setDimensionEdit] = useState<string | null>(null);
  const [metricEdit, setMetricEdit] = useState<string | null>(null);
  // The id that was REFUSED, not a flag: the message names an index, and a flag left it renaming
  // itself to whatever the input said afterwards, blaming an id the server never saw.
  const [refusedId, setRefusedId] = useState<string | null>(null);
  const defaults = vectorIndexDefaults(provider);
  const dimension = dimensionEdit ?? defaults.dimension;
  const metric = metricEdit ?? defaults.metric;
  const providerEnabled = provider ? provider.enabled : null;
  const dimensionValid = isValidVectorDimension(dimension);

  const create = useMutation({
    mutationFn: () =>
      createIndex(instance, {
        uniqueId: indexId.trim(),
        pluginType: "VectorIndex",
        pluginOptions: vectorIndexPluginOptions({ dimension, metric, embeddingName }),
      }),
    onSuccess: (ok) => {
      // The server answers a boolean: false means "not created" (the id exists, or the options
      // are invalid). Reporting that as success would leave the picker empty with no reason.
      const submitted = indexId.trim();
      setRefusedId(ok ? null : submitted);
      if (!ok) return;
      queryClient.invalidateQueries({ queryKey: [instance.id, "status"] });
      onCreated(submitted);
    },
  });

  if (providerEnabled !== true) {
    return (
      <p className="text-warn basis-full text-[11px]" data-testid="semantic-onramp-provider-off">
        A semantic search needs a vector index to rank against, and this instance has none.
        {providerEnabled === null
          ? " This server also does not report an embedding provider, so it cannot turn typed words into a vector either."
          : " The embedding provider is also off here, so it cannot turn typed words into a vector either."}{" "}
        Create the index on the Indexes screen and paste your own vectors under 'ask an index'.
      </p>
    );
  }

  return (
    <div className="basis-full space-y-2" data-testid="semantic-onramp">
      <p className="text-fg-dim text-[12px]">
        A semantic search ranks against a vector index bound to a named embedding, and this
        instance has none yet. A bound index maintains itself: it projects the embedding already
        on your elements, so creating it here is enough to search.
      </p>
      <div className="flex flex-wrap items-end gap-2">
        <Field helpKey="indexId" label="index id" htmlFor="onramp-index-id">
          <input
            id="onramp-index-id"
            data-testid="onramp-index-id"
            className="input w-40"
            value={indexId}
            onChange={(e) => setIndexId(e.target.value)}
            placeholder="embeddings"
          />
        </Field>
        {/* Its own help key, not the Indexes screen's: that one ends "leave empty for a raw
            index", which is true there and wrong here, where an unbound index would rank
            nothing and the create button is gated on this field being filled. */}
        <Field
          helpKey="semanticBindEmbedding"
          label="bind embedding"
          htmlFor="onramp-embedding-name"
        >
          <input
            id="onramp-embedding-name"
            data-testid="onramp-embedding-name"
            className="input w-32"
            list="shape-embedding-names"
            value={embeddingName}
            onChange={(e) => setEmbeddingName(e.target.value)}
            placeholder="default"
          />
        </Field>
        <Field helpKey="vectorDimension" label="dimension" htmlFor="onramp-dimension">
          <input
            id="onramp-dimension"
            data-testid="onramp-dimension"
            className="input w-24"
            type="number"
            min={1}
            max={4096}
            value={dimension}
            onChange={(e) => setDimensionEdit(e.target.value)}
          />
          {!dimensionValid && (
            <div className="text-warn text-[11px]" data-testid="onramp-dimension-invalid">
              a whole number from 1 to 4096
            </div>
          )}
        </Field>
        <Field helpKey="vectorMetric" label="metric" htmlFor="onramp-metric">
          <select
            id="onramp-metric"
            data-testid="onramp-metric"
            className="input w-auto"
            value={metric}
            onChange={(e) => setMetricEdit(e.target.value)}
          >
            <option>Cosine</option>
            <option>DotProduct</option>
            <option>L2</option>
          </select>
        </Field>
        <button
          type="button"
          className="btn btn-accent"
          data-testid="onramp-create"
          disabled={
            !indexId.trim() || !embeddingName.trim() || !dimensionValid || create.isPending
          }
          onClick={() => create.mutate()}
        >
          {create.isPending ? "Creating…" : "Create and search"}
        </button>
      </div>
      {/* The provider's OWN numbers, never the ones in the fields: interpolating the editable
          values made this sentence attribute a hand-typed dimension to the provider. And the
          `providerReady` flag exists precisely so a half-configured provider (enabled, no
          dimension) is not credited with the fallback either. */}
      {defaults.providerReady ? (
        <p className="text-fg-faint text-[11px]" data-testid="onramp-provider-note">
          Prefilled from this instance's embedding provider:{" "}
          {provider?.modelName ?? "model not named"} at {defaults.dimension} dimensions,{" "}
          {defaults.metric}. An index whose dimension disagrees with whatever writes into it is
          refused on every embed and every search, so change these only for vectors from
          somewhere else.
        </p>
      ) : (
        <p className="text-warn text-[11px]" data-testid="onramp-provider-unnamed">
          This instance's provider is on but does not report a dimension, so the numbers above
          are defaults rather than its own. An index whose dimension disagrees with whatever
          writes into it is refused on every embed and every search, so check them against the
          model before creating.
        </p>
      )}
      {refusedId && (
        <p className="text-warn text-[11px]" data-testid="onramp-refused">
          '{refusedId}' was NOT created. The id may already exist, or the options are invalid for
          this server.
        </p>
      )}
      {create.isError && <ErrorBox error={create.error} />}
    </div>
  );
}
