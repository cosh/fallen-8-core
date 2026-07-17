import { useEffect, useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useNavigate } from "@tanstack/react-router";
import { useActiveInstance } from "../instances/registry";
import {
  createSubGraph,
  deleteSubGraph,
  getSubGraphContents,
  listSubGraphSummaries,
  recalculateSubGraph,
} from "../api/endpoints";
import type { PatternSpecification, SubGraphSpecification } from "../api/types";
import { ApiError } from "../api/client";
import { validatePatternSequence } from "../lib/patternValidation";
import { normalizePatterns, subGraphBlock } from "../lib/storedQueries";
import { DelegateSlot } from "../delegate/DelegateSlot";
import {
  FilterSourceToggle,
  SaveAsStoredQuery,
  StoredQueryPicker,
} from "../components/StoredQueryControls";
import { ErrorBox } from "../components/ErrorBox";
import { getInstanceStore, type FilterSource } from "../state/instanceStore";

/**
 * Subgraph studio (FR-15/16/17): lifecycle + pattern builder. The builder enforces the
 * alternation rules client-side (lib/patternValidation) and still surfaces the server's
 * 400. Status codes map to distinct, actionable messages; an EMPTY subgraph is a valid
 * 201 outcome, not an error. Filters/patterns come inline or from a stored query of
 * kind SubGraph — the source toggle keeps the two mutually exclusive (concept spec §5.1).
 */

interface PatternDraft extends PatternSpecification {
  key: string;
}

function newPattern(type: PatternSpecification["type"]): PatternDraft {
  return {
    key: `p-${Date.now().toString(36)}-${Math.random().toString(36).slice(2, 6)}`,
    type,
    patternName: "",
    direction: "OutgoingEdge",
    minLength: 1,
    maxLength: 3,
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
  const instance = useActiveInstance()!;
  const store = getInstanceStore(instance.id);
  const mergeIntoCanvas = store((s) => s.mergeIntoCanvas);
  const queryClient = useQueryClient();
  const navigate = useNavigate();

  const [name, setName] = useState("");
  const [fromSubGraph, setFromSubGraph] = useState("");
  const [vertexFilter, setVertexFilter] = useState("");
  const [edgeFilter, setEdgeFilter] = useState("");
  const [patterns, setPatterns] = useState<PatternDraft[]>([]);
  const [filterSource, setFilterSource] = useState<FilterSource>("inline");
  const [storedQuery, setStoredQuery] = useState("");
  const [message, setMessage] = useState<string | null>(null);

  // Consume a one-shot prefill (Dashboard → Stored queries → "Open in Subgraph").
  const subgraphPrefill = store((s) => s.subgraphPrefill);
  const setSubgraphPrefill = store((s) => s.setSubgraphPrefill);
  useEffect(() => {
    if (subgraphPrefill) {
      setFilterSource("stored");
      setStoredQuery(subgraphPrefill.storedQuery);
      setSubgraphPrefill(null);
    }
  }, [subgraphPrefill, setSubgraphPrefill]);

  const sequenceError = filterSource === "inline" ? validatePatternSequence(patterns) : null;

  const list = useQuery({
    queryKey: [instance.id, "subgraphs"],
    queryFn: ({ signal }) => listSubGraphSummaries(instance, signal),
  });

  const invalidate = () =>
    queryClient.invalidateQueries({ queryKey: [instance.id, "subgraphs"] });

  const create = useMutation({
    mutationFn: async () => {
      // Stored and inline fragments never travel together (server 400s on mix).
      const spec: SubGraphSpecification =
        filterSource === "stored"
          ? { name: name.trim(), storedQuery }
          : {
              name: name.trim(),
              fromSubGraph: fromSubGraph.trim() || undefined,
              vertexFilter: vertexFilter || undefined,
              edgeFilter: edgeFilter || undefined,
              patterns: normalizePatterns(patterns),
            };
      return await createSubGraph(instance, spec);
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

  const updatePattern = (key: string, patch: Partial<PatternDraft>) =>
    setPatterns((previous) =>
      previous.map((pattern) => (pattern.key === key ? { ...pattern, ...patch } : pattern)),
    );

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
                <td className="table-cell font-semibold">{subgraph.name}</td>
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
        <div className="panel-title">Create subgraph</div>
        <form
          className="space-y-3 p-3"
          onSubmit={(e) => {
            e.preventDefault();
            setMessage(null);
            create.mutate();
          }}
        >
          <div className="flex flex-wrap items-end gap-3">
            <div>
              <label className="label" htmlFor="sg-name">
                name
              </label>
              <input
                id="sg-name"
                data-testid="sg-name"
                className="input w-48"
                value={name}
                onChange={(e) => setName(e.target.value)}
              />
            </div>
            <div>
              <label className="label" htmlFor="sg-from">
                fromSubGraph (optional nesting)
              </label>
              <input
                id="sg-from"
                className="input w-48"
                value={fromSubGraph}
                onChange={(e) => setFromSubGraph(e.target.value)}
              />
            </div>
          </div>

          <FilterSourceToggle value={filterSource} onChange={setFilterSource} />

          {filterSource === "stored" && (
            <StoredQueryPicker
              instance={instance}
              kind="SubGraph"
              value={storedQuery}
              onChange={setStoredQuery}
            />
          )}

          {filterSource === "inline" && (
          <>
          <div className="space-y-2">
            <DelegateSlot
              instance={instance}
              delegateKind="GraphElementFilter"
              label="vertexFilter (top level)"
              contextLabel={`Subgraph · ${name || "unnamed"}`}
              value={vertexFilter}
              onChange={setVertexFilter}
            />
            <DelegateSlot
              instance={instance}
              delegateKind="GraphElementFilter"
              label="edgeFilter (top level)"
              contextLabel={`Subgraph · ${name || "unnamed"}`}
              value={edgeFilter}
              onChange={setEdgeFilter}
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
                    <div>
                      <label className="label" htmlFor={`pt-${pattern.key}`}>
                        type
                      </label>
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
                    </div>
                    <div>
                      <label className="label" htmlFor={`pn-${pattern.key}`}>
                        name
                      </label>
                      <input
                        id={`pn-${pattern.key}`}
                        className="input w-32"
                        value={pattern.patternName ?? ""}
                        onChange={(e) =>
                          updatePattern(pattern.key, { patternName: e.target.value })
                        }
                      />
                    </div>
                    {pattern.type !== "Vertex" && (
                      <div>
                        <label className="label" htmlFor={`pd-${pattern.key}`}>
                          direction
                        </label>
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
                      </div>
                    )}
                    {pattern.type === "VariableLengthEdge" && (
                      <>
                        <div>
                          <label className="label" htmlFor={`pmin-${pattern.key}`}>
                            min
                          </label>
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
                        </div>
                        <div>
                          <label className="label" htmlFor={`pmax-${pattern.key}`}>
                            max (≤100)
                          </label>
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
                        </div>
                      </>
                    )}
                    <button
                      type="button"
                      className="btn btn-danger ml-auto"
                      onClick={() =>
                        setPatterns((previous) =>
                          previous.filter((p) => p.key !== pattern.key),
                        )
                      }
                    >
                      Remove
                    </button>
                  </div>

                  <div className="space-y-1">
                    {pattern.type === "Vertex" ? (
                      <>
                        <DelegateSlot
                          instance={instance}
                          delegateKind="GraphElementFilter"
                          label="graphElementFilter"
                          contextLabel={`Subgraph pattern #${index + 1}`}
                          value={pattern.graphElementFilter ?? ""}
                          onChange={(fragment) =>
                            updatePattern(pattern.key, { graphElementFilter: fragment })
                          }
                        />
                        <DelegateSlot
                          instance={instance}
                          delegateKind="VertexFilter"
                          label="vertexFilter"
                          contextLabel={`Subgraph pattern #${index + 1}`}
                          value={pattern.vertexFilter ?? ""}
                          onChange={(fragment) =>
                            updatePattern(pattern.key, { vertexFilter: fragment })
                          }
                        />
                      </>
                    ) : (
                      <>
                        <DelegateSlot
                          instance={instance}
                          delegateKind="GraphElementFilter"
                          label="graphElementFilter"
                          contextLabel={`Subgraph pattern #${index + 1}`}
                          value={pattern.graphElementFilter ?? ""}
                          onChange={(fragment) =>
                            updatePattern(pattern.key, { graphElementFilter: fragment })
                          }
                        />
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
                  onClick={() => setPatterns((p) => [...p, newPattern("Vertex")])}
                >
                  + Vertex step
                </button>
                <button
                  type="button"
                  className="btn"
                  data-testid="add-edge-step"
                  onClick={() => setPatterns((p) => [...p, newPattern("Edge")])}
                >
                  + Edge step
                </button>
                <button
                  type="button"
                  className="btn"
                  onClick={() => setPatterns((p) => [...p, newPattern("VariableLengthEdge")])}
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

          <SaveAsStoredQuery
            instance={instance}
            kind="SubGraph"
            buildBlock={() =>
              subGraphBlock(vertexFilter, edgeFilter, normalizePatterns(patterns))
            }
            disabled={!vertexFilter && !edgeFilter && patterns.length === 0}
            disabledReason="add a filter or a pattern step first"
            onSaved={(savedName) => {
              setFilterSource("stored");
              setStoredQuery(savedName);
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
                  <pre className="mt-1 whitespace-pre-wrap">{body}</pre>
                </div>
              );
            })()}
          </div>
        )}
      </section>
    </div>
  );
}
