import { useEffect, useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useNavigate } from "@tanstack/react-router";
import { useInstanceStore } from "../instances/registry";
import {
  createSubGraph,
  deleteSubGraph,
  getSubGraphContents,
  listSubGraphSummaries,
  recalculateSubGraph,
} from "../api/endpoints";
import type {
  PatternSpecification,
  SemanticTraversalSpecification,
  SubGraphSpecification,
} from "../api/types";
import { ApiError } from "../api/client";
import { validatePatternSequence } from "../lib/patternValidation";
import { normalizePatterns, subGraphBlock } from "../lib/storedQueries";
import {
  buildSemanticQuery,
  describeSemanticSummary,
  parseThreshold,
  type SemanticQueryDraft,
} from "../lib/semantic";
import { shapeSuggestions, useEmbeddingProvider, useGraphShape } from "../state/graphShape";
import { SemanticQueryEditor } from "../components/SemanticQueryEditor";
import { VertexFilterSlot } from "../components/VertexFilterSlot";
import { DelegateSlot } from "../delegate/DelegateSlot";
import {
  FilterSourceToggle,
  SaveAsStoredQuery,
  StoredQueryPicker,
} from "../components/StoredQueryControls";
import { ErrorBox } from "../components/ErrorBox";
import { Field } from "../components/Field";
import { Truncated } from "../components/Truncated";
import { DISPLAY_CAP } from "../lib/truncate";
import { type SubgraphPatternDraft } from "../state/instanceStore";

/**
 * Subgraph studio (FR-15/16/17): lifecycle + pattern builder. The builder enforces the
 * alternation rules client-side (lib/patternValidation) and still surfaces the server's
 * 400. Status codes map to distinct, actionable messages; an EMPTY subgraph is a valid
 * 201 outcome, not an error. Filters/patterns come inline or from a stored query of
 * kind SubGraph — the source toggle keeps the two mutually exclusive (concept spec §5.1).
 */

function newPattern(type: PatternSpecification["type"]): SubgraphPatternDraft {
  return {
    key: `p-${Date.now().toString(36)}-${Math.random().toString(36).slice(2, 6)}`,
    type,
    patternName: "",
    direction: "OutgoingEdge",
    minLength: 1,
    maxLength: 3,
    filterMode: "everything",
    semanticMinScore: "0.7",
  };
}

function describeCreateError(error: unknown): { title: string; body: string } {
  if (error instanceof ApiError) {
    switch (error.status) {
      case 400:
        return {
          title: "The server rejected the specification (400)",
          body:
            error.body ||
            "Compile or validation failure - check the fragments and the pattern sequence.",
        };
      case 404:
        return {
          title: "Source subgraph not found (404)",
          body: "The fromSubGraph you referenced does not exist on this instance.",
        };
      case 403:
        return {
          title: "Dynamic code execution is off (403)",
          body: "Inline fragments cannot run on this instance — use a stored query (kind SubGraph) instead.",
        };
      case 409:
        return {
          title: "Conflict (409)",
          body:
            error.body ||
            "A subgraph with this name already exists, or a quota (subgraph count / materialized elements) is exhausted.",
        };
    }
  }
  return {
    title: "Create failed",
    body: error instanceof Error ? error.message : String(error),
  };
}

export function SubgraphScreen() {
  const { instance, store } = useInstanceStore();
  const mergeIntoCanvas = store((s) => s.mergeIntoCanvas);
  const queryClient = useQueryClient();
  const navigate = useNavigate();

  // The whole "Create subgraph" form lives in the persisted per-instance store, so leaving
  // for the Canvas and returning restores it exactly (feature index-workspace / studio state
  // persistence). Transient result messaging stays local (below).
  const draft = store((s) => s.subgraphDraft);
  const setSubgraphDraft = store((s) => s.setSubgraphDraft);
  const resetSubgraphDraft = store((s) => s.resetSubgraphDraft);
  const {
    name,
    fromSubGraph,
    vertexFilterMode,
    vertexFilter,
    vertexMinScore,
    edgeFilter,
    patterns,
    filterSource,
    storedQuery,
    semanticQuery,
  } = draft;
  const [message, setMessage] = useState<string | null>(null);

  const shape = useGraphShape(instance).data;
  const suggestions = shapeSuggestions(shape);
  const provider = useEmbeddingProvider(instance);
  const providerEnabled = provider ? provider.enabled : null;
  const patchSemanticQuery = (patch: Partial<SemanticQueryDraft>) =>
    setSubgraphDraft({ semanticQuery: { ...semanticQuery, ...patch } });

  // The semantic QUERY section exists exactly while some vertex slot is in semantic mode
  // (feature subgraph-semantic-thresholds) - an inert semantic configuration is
  // unrepresentable. Thresholds are validated per slot; the query is validated once.
  const semanticSlotActive =
    filterSource === "inline" &&
    (vertexFilterMode === "semantic" ||
      patterns.some((p) => p.type === "Vertex" && p.filterMode === "semantic"));
  const thresholdInvalid =
    filterSource === "inline" &&
    ((vertexFilterMode === "semantic" && parseThreshold(vertexMinScore) === undefined) ||
      patterns.some(
        (p) =>
          p.type === "Vertex" &&
          p.filterMode === "semantic" &&
          parseThreshold(p.semanticMinScore) === undefined,
      ));
  const semanticQueryBuild = semanticSlotActive
    ? buildSemanticQuery(semanticQuery, { providerEnabled })
    : null;

  // Consume a one-shot prefill (Dashboard → Stored queries → "Open in Subgraph").
  const subgraphPrefill = store((s) => s.subgraphPrefill);
  const setSubgraphPrefill = store((s) => s.setSubgraphPrefill);
  useEffect(() => {
    if (subgraphPrefill) {
      setSubgraphDraft({ filterSource: "stored", storedQuery: subgraphPrefill.storedQuery });
      setSubgraphPrefill(null);
    }
  }, [subgraphPrefill, setSubgraphPrefill, setSubgraphDraft]);

  const sequenceError = filterSource === "inline" ? validatePatternSequence(patterns) : null;

  const list = useQuery({
    queryKey: [instance.id, "subgraphs"],
    queryFn: ({ signal }) => listSubGraphSummaries(instance, signal),
  });

  const invalidate = () =>
    queryClient.invalidateQueries({ queryKey: [instance.id, "subgraphs"] });

  const create = useMutation({
    mutationFn: async () => {
      // Stored and inline fragments never travel together (server 400s on mix);
      // fromSubGraph is per-request scoping and rides along as a query param either way.
      let spec: SubGraphSpecification;
      if (filterSource === "stored") {
        // Stored mode has no slots, so no semantic query travels (a stored-template
        // invocation cannot carry one - server 400s).
        spec = { name: name.trim(), storedQuery };
      } else {
        // Each vertex slot sends exactly what its MODE says; the semantic query - pure
        // data, bound at registration - travels only while some slot consumes it. The
        // one-owner-per-slot 400 is structurally unreachable from here.
        let semantic: SemanticTraversalSpecification | undefined;
        if (semanticSlotActive) {
          const query = buildSemanticQuery(semanticQuery, { providerEnabled });
          if (!query.ok) {
            throw new ApiError(400, "/subgraph", `Semantic query: ${query.error}`);
          }
          semantic = query.spec;
          if (vertexFilterMode === "semantic") {
            // Gating (thresholdInvalid) guarantees this parses at submit time.
            semantic.minScore = parseThreshold(vertexMinScore);
          }
        }
        spec = {
          name: name.trim(),
          vertexFilter:
            vertexFilterMode === "fragment" ? vertexFilter || undefined : undefined,
          edgeFilter: edgeFilter || undefined,
          patterns: normalizePatterns(patterns),
          ...(semantic ? { semantic } : {}),
        };
      }
      return await createSubGraph(instance, spec, fromSubGraph.trim() || undefined);
    },
    onSuccess: (summary) => {
      setMessage(
        summary
          ? `Created '${summary.name}': ${summary.vertexCount} vertices, ${summary.edgeCount} edges.` +
              (summary.vertexCount === 0 && summary.edgeCount === 0
                ? " (Empty is a valid result - the pattern simply matched nothing.)"
                : "")
          : "Created.",
      );
      invalidate();
    },
  });

  const recalculate = useMutation({
    mutationFn: (subgraphName: string) => recalculateSubGraph(instance, subgraphName),
    onSuccess: (summary) => {
      setMessage(
        summary
          ? `Recalculated '${summary.name}': ${summary.vertexCount} vertices, ${summary.edgeCount} edges.`
          : "Recalculated.",
      );
      invalidate();
    },
  });

  const remove = useMutation({
    mutationFn: (subgraphName: string) => deleteSubGraph(instance, subgraphName),
    onSuccess: () => {
      setMessage("Deleted.");
      invalidate();
    },
  });

  const toCanvas = useMutation({
    mutationFn: async (subgraphName: string) => {
      const contents = await getSubGraphContents(instance, subgraphName);
      if (contents) {
        mergeIntoCanvas(contents.vertices, contents.edges);
        navigate({ to: "/canvas" });
      }
    },
  });

  const updatePattern = (key: string, patch: Partial<SubgraphPatternDraft>) =>
    setSubgraphDraft({
      patterns: patterns.map((pattern) =>
        pattern.key === key ? { ...pattern, ...patch } : pattern,
      ),
    });

  return (
    <div className="mx-auto max-w-5xl space-y-4">
      <section className="panel">
        <div className="panel-title">Subgraphs</div>
        {list.isError && (
          <div className="p-3">
            <ErrorBox error={list.error} onRetry={() => list.refetch()} />
          </div>
        )}
        <table className="w-full text-[12px]">
          <thead>
            <tr className="text-fg-faint">
              <th className="table-cell">name</th>
              <th className="table-cell">vertices</th>
              <th className="table-cell">edges</th>
              <th className="table-cell w-64">actions</th>
            </tr>
          </thead>
          <tbody>
            {(list.data ?? []).map((subgraph) => (
              <tr key={subgraph.name}>
                <td className="table-cell font-semibold">
                  <Truncated text={subgraph.name} max={DISPLAY_CAP.name} />
                  {subgraph.semantic && (
                    <span
                      className="text-accent border-line ml-2 rounded border px-1 py-0.5 align-middle text-[10px] font-normal"
                      data-testid={`sg-semantic-badge-${subgraph.name}`}
                      title={describeSemanticSummary(subgraph.semantic)}
                    >
                      semantic
                    </span>
                  )}
                </td>
                <td className="table-cell">{subgraph.vertexCount}</td>
                <td className="table-cell">{subgraph.edgeCount}</td>
                <td className="table-cell">
                  <div className="flex gap-1">
                    <button
                      type="button"
                      className="btn"
                      onClick={() => toCanvas.mutate(subgraph.name)}
                    >
                      To canvas
                    </button>
                    <button
                      type="button"
                      className="btn"
                      onClick={() => recalculate.mutate(subgraph.name)}
                    >
                      Recalculate
                    </button>
                    <button
                      type="button"
                      className="btn btn-danger"
                      onClick={() => remove.mutate(subgraph.name)}
                    >
                      Delete
                    </button>
                  </div>
                </td>
              </tr>
            ))}
            {(list.data ?? []).length === 0 && !list.isError && (
              <tr>
                <td className="table-cell text-fg-faint" colSpan={4}>
                  no subgraphs yet
                </td>
              </tr>
            )}
          </tbody>
        </table>
        {message && (
          <div className="text-accent p-3 text-[12px]" data-testid="subgraph-message">
            {message}
          </div>
        )}
      </section>

      <section className="panel">
        <div className="panel-title">
          Create subgraph
          <button
            type="button"
            className="btn ml-auto"
            data-testid="subgraph-clear"
            title="Reset every subgraph input to its default"
            onClick={() => {
              resetSubgraphDraft();
              setMessage(null);
            }}
          >
            Clear
          </button>
        </div>
        <form
          className="space-y-3 p-3"
          onSubmit={(e) => {
            e.preventDefault();
            setMessage(null);
            create.mutate();
          }}
        >
          <div className="flex flex-wrap items-end gap-3">
            <Field helpKey="subgraphName" label="name" htmlFor="sg-name">
              <input
                id="sg-name"
                data-testid="sg-name"
                className="input w-48"
                value={name}
                onChange={(e) => setSubgraphDraft({ name: e.target.value })}
              />
            </Field>
            <Field
              helpKey="subgraphFrom"
              label="fromSubGraph (optional nesting)"
              htmlFor="sg-from"
            >
              <input
                id="sg-from"
                className="input w-48"
                value={fromSubGraph}
                onChange={(e) => setSubgraphDraft({ fromSubGraph: e.target.value })}
              />
            </Field>
          </div>

          <FilterSourceToggle
            value={filterSource}
            onChange={(value) => setSubgraphDraft({ filterSource: value })}
          />

          {filterSource === "stored" && (
            <StoredQueryPicker
              instance={instance}
              kind="SubGraph"
              value={storedQuery}
              onChange={(value) => setSubgraphDraft({ storedQuery: value })}
            />
          )}

          {filterSource === "inline" && (
          <>
          {/* The semantic QUERY appears exactly while a vertex slot consumes it (feature
              subgraph-semantic-thresholds): one query per request, bound at creation. */}
          {semanticSlotActive && (
            <div className="border-line rounded border" data-testid="sg-semantic-query">
              <div className="panel-title">
                semantic query
                <span className="text-fg-faint normal-case">
                  one query per request · resolved once at creation and stored with the
                  subgraph — recalculate reuses it, text is never re-embedded
                </span>
              </div>
              <div className="space-y-2 p-2">
                <SemanticQueryEditor
                  query={semanticQuery}
                  onChange={patchSemanticQuery}
                  providerEnabled={providerEnabled}
                  embeddingNames={suggestions.embeddingNames}
                  idPrefix="sg"
                />
                {semanticQueryBuild && !semanticQueryBuild.ok && (
                  <p className="text-warn text-[11px]" data-testid="sg-sem-error">
                    {semanticQueryBuild.error}
                  </p>
                )}
              </div>
            </div>
          )}

          <div className="space-y-2">
            <VertexFilterSlot
              instance={instance}
              idPrefix="sg-vf"
              label="vertexFilter (top level)"
              contextLabel={`Subgraph · ${name || "unnamed"}`}
              mode={vertexFilterMode}
              onModeChange={(mode) => setSubgraphDraft({ vertexFilterMode: mode })}
              fragment={vertexFilter}
              onFragmentChange={(value) => setSubgraphDraft({ vertexFilter: value })}
              minScore={vertexMinScore}
              onMinScoreChange={(value) => setSubgraphDraft({ vertexMinScore: value })}
              metric={semanticQuery.metric}
            />
            <DelegateSlot
              instance={instance}
              delegateKind="EdgeFilter"
              label="edgeFilter (top level)"
              contextLabel={`Subgraph · ${name || "unnamed"}`}
              value={edgeFilter}
              onChange={(value) => setSubgraphDraft({ edgeFilter: value })}
            />
          </div>

          <div className="border-line rounded border">
            <div className="panel-title">
              pattern sequence
              <span className="text-fg-faint normal-case">
                vertex ↔ edge alternation, starts with a vertex (level-0 edge legal)
              </span>
            </div>
            <div className="space-y-2 p-2">
              {patterns.map((pattern, index) => (
                <div key={pattern.key} className="panel space-y-2 p-2">
                  <div className="flex flex-wrap items-end gap-2">
                    <span className="text-fg-faint text-[11px]">#{index + 1}</span>
                    <Field helpKey="patternType" label="type" htmlFor={`pt-${pattern.key}`}>
                      <select
                        id={`pt-${pattern.key}`}
                        className="input w-auto"
                        value={pattern.type}
                        onChange={(e) =>
                          updatePattern(pattern.key, {
                            type: e.target.value as PatternSpecification["type"],
                          })
                        }
                      >
                        <option>Vertex</option>
                        <option>Edge</option>
                        <option>VariableLengthEdge</option>
                      </select>
                    </Field>
                    <Field helpKey="patternName" label="name" htmlFor={`pn-${pattern.key}`}>
                      <input
                        id={`pn-${pattern.key}`}
                        className="input w-32"
                        value={pattern.patternName ?? ""}
                        onChange={(e) =>
                          updatePattern(pattern.key, { patternName: e.target.value })
                        }
                      />
                    </Field>
                    {pattern.type !== "Vertex" && (
                      <Field
                        helpKey="patternDirection"
                        label="direction"
                        htmlFor={`pd-${pattern.key}`}
                      >
                        <select
                          id={`pd-${pattern.key}`}
                          className="input w-auto"
                          value={pattern.direction}
                          onChange={(e) =>
                            updatePattern(pattern.key, {
                              direction: e.target
                                .value as PatternSpecification["direction"],
                            })
                          }
                        >
                          <option>OutgoingEdge</option>
                          <option>IncomingEdge</option>
                          <option>UndirectedEdge</option>
                        </select>
                      </Field>
                    )}
                    {pattern.type === "VariableLengthEdge" && (
                      <>
                        <Field
                          helpKey="patternMinLength"
                          label="min"
                          htmlFor={`pmin-${pattern.key}`}
                        >
                          <input
                            id={`pmin-${pattern.key}`}
                            className="input w-16"
                            type="number"
                            min={0}
                            value={pattern.minLength ?? 1}
                            onChange={(e) =>
                              updatePattern(pattern.key, {
                                minLength: Number(e.target.value),
                              })
                            }
                          />
                        </Field>
                        <Field
                          helpKey="patternMaxLength"
                          label="max (≤100)"
                          htmlFor={`pmax-${pattern.key}`}
                        >
                          <input
                            id={`pmax-${pattern.key}`}
                            className="input w-16"
                            type="number"
                            min={0}
                            max={100}
                            value={pattern.maxLength ?? 3}
                            onChange={(e) =>
                              updatePattern(pattern.key, {
                                maxLength: Number(e.target.value),
                              })
                            }
                          />
                        </Field>
                      </>
                    )}
                    <button
                      type="button"
                      className="btn btn-danger ml-auto"
                      onClick={() =>
                        setSubgraphDraft({
                          patterns: patterns.filter((p) => p.key !== pattern.key),
                        })
                      }
                    >
                      Remove
                    </button>
                  </div>

                  <div className="space-y-1">
                    {pattern.type === "Vertex" ? (
                      <VertexFilterSlot
                        instance={instance}
                        idPrefix={`sg-p${index}-vf`}
                        label="vertexFilter"
                        contextLabel={`Subgraph pattern #${index + 1}`}
                        mode={pattern.filterMode}
                        onModeChange={(mode) =>
                          updatePattern(pattern.key, { filterMode: mode })
                        }
                        fragment={pattern.vertexFilter ?? ""}
                        onFragmentChange={(fragment) =>
                          updatePattern(pattern.key, { vertexFilter: fragment })
                        }
                        minScore={pattern.semanticMinScore}
                        onMinScoreChange={(value) =>
                          updatePattern(pattern.key, { semanticMinScore: value })
                        }
                        metric={semanticQuery.metric}
                      />
                    ) : (
                      <>
                        <DelegateSlot
                          instance={instance}
                          delegateKind="EdgeFilter"
                          label="edgeFilter"
                          contextLabel={`Subgraph pattern #${index + 1}`}
                          value={pattern.edgeFilter ?? ""}
                          onChange={(fragment) =>
                            updatePattern(pattern.key, { edgeFilter: fragment })
                          }
                        />
                        <DelegateSlot
                          instance={instance}
                          delegateKind="EdgePropertyFilter"
                          label="edgePropertyFilter"
                          contextLabel={`Subgraph pattern #${index + 1}`}
                          value={pattern.edgePropertyFilter ?? ""}
                          onChange={(fragment) =>
                            updatePattern(pattern.key, { edgePropertyFilter: fragment })
                          }
                        />
                      </>
                    )}
                  </div>
                </div>
              ))}

              <div className="flex gap-2">
                <button
                  type="button"
                  className="btn"
                  data-testid="add-vertex-step"
                  onClick={() =>
                    setSubgraphDraft({ patterns: [...patterns, newPattern("Vertex")] })
                  }
                >
                  + Vertex step
                </button>
                <button
                  type="button"
                  className="btn"
                  data-testid="add-edge-step"
                  onClick={() =>
                    setSubgraphDraft({ patterns: [...patterns, newPattern("Edge")] })
                  }
                >
                  + Edge step
                </button>
                <button
                  type="button"
                  className="btn"
                  onClick={() =>
                    setSubgraphDraft({
                      patterns: [...patterns, newPattern("VariableLengthEdge")],
                    })
                  }
                >
                  + Variable-length edge
                </button>
              </div>

              {sequenceError && (
                <div className="text-danger text-[12px]" data-testid="sequence-error">
                  {sequenceError}
                </div>
              )}
            </div>
          </div>

          {/* A template binds its delegates at ITS registration, where no semantic query
              exists - the server 400s thresholds in templates, so saving is blocked with
              the reason instead of silently dropping the semantic parts. */}
          <SaveAsStoredQuery
            instance={instance}
            kind="SubGraph"
            buildBlock={() =>
              subGraphBlock(
                vertexFilterMode === "fragment" ? vertexFilter : "",
                edgeFilter,
                normalizePatterns(patterns),
              )
            }
            disabled={
              semanticSlotActive ||
              ((vertexFilterMode !== "fragment" || !vertexFilter) &&
                !edgeFilter &&
                patterns.length === 0)
            }
            disabledReason={
              semanticSlotActive
                ? "semantic thresholds cannot ride a stored template — it has no query to bind"
                : "add a filter or a pattern step first"
            }
            onSaved={(savedName) => {
              setSubgraphDraft({ filterSource: "stored", storedQuery: savedName });
            }}
          />
          </>
          )}

          <button
            type="submit"
            className="btn btn-accent"
            data-testid="sg-create"
            disabled={
              !name.trim() ||
              sequenceError !== null ||
              (filterSource === "stored" && !storedQuery) ||
              (semanticQueryBuild !== null && !semanticQueryBuild.ok) ||
              thresholdInvalid ||
              create.isPending
            }
            title={
              filterSource === "stored" && !storedQuery
                ? "pick a stored query first"
                : undefined
            }
          >
            {create.isPending ? "Creating…" : "Create subgraph"}
          </button>
        </form>

        {create.isError && (
          <div className="px-3 pb-3" data-testid="create-error">
            {(() => {
              const { title, body } = describeCreateError(create.error);
              return (
                <div className="border-danger/40 text-danger rounded border p-2 text-[12px]">
                  <div className="font-semibold">{title}</div>
                  <pre className="mt-1 wrap-break-word whitespace-pre-wrap">{body}</pre>
                </div>
              );
            })()}
          </div>
        )}
      </section>
    </div>
  );
}
