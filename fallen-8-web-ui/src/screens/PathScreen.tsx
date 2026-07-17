import { useState } from "react";
import { useMutation } from "@tanstack/react-query";
import { useNavigate } from "@tanstack/react-router";
import { useActiveInstance } from "../instances/registry";
import { findPaths, getGraphElement } from "../api/endpoints";
import { ApiError } from "../api/client";
import type { PathREST, VertexREST } from "../api/types";
import { getInstanceStore } from "../state/instanceStore";
import {
  buildPathSpecification,
  hasAnyPathFragment,
  pathBlockFromDraft,
} from "../lib/storedQueries";
import {
  buildSemanticSpec,
  semanticOwnsVertexCost,
  semanticOwnsVertexFilter,
  type SemanticDraft,
} from "../lib/semantic";
import { embeddingProvider, shapeSuggestions, useGraphShape } from "../state/graphShape";
import { SemanticBlockEditor } from "../components/SemanticBlockEditor";
import { DelegateSlot } from "../delegate/DelegateSlot";
import {
  FilterSourceToggle,
  SaveAsStoredQuery,
  StoredQueryPicker,
} from "../components/StoredQueryControls";
import { ErrorBox } from "../components/ErrorBox";
import { Field } from "../components/Field";

/**
 * Path finder (FR-12/13/14): BLS vs Dijkstra with the explainer, defaults pre-filled,
 * the five delegate slots as the advanced tier (all optional, empty by default), results
 * as ordered element lists + canvas overlay. Fragments were already validated in the
 * editor - /path swallows compile errors, so an empty result here is "no paths found",
 * never an error (FR-13). Filters come from inline fragments or a stored query — the
 * source toggle keeps the two mutually exclusive (concept spec §5.1).
 */
export function PathScreen() {
  const instance = useActiveInstance()!;
  const store = getInstanceStore(instance.id);
  const draft = store((s) => s.pathDraft);
  const setDraft = store((s) => s.setPathDraft);
  const setPathOverlay = store((s) => s.setPathOverlay);
  const mergeIntoCanvas = store((s) => s.mergeIntoCanvas);
  const navigate = useNavigate();
  const shape = useGraphShape(instance).data;
  const suggestions = shapeSuggestions(shape);
  const provider = embeddingProvider(shape);
  const providerEnabled = provider ? provider.enabled : null;
  const [showAdvanced, setShowAdvanced] = useState(
    Boolean(
      draft.vertexFilter ||
        draft.edgeFilter ||
        draft.edgePropertyFilter ||
        draft.vertexCost ||
        draft.edgeCost ||
        draft.semantic.enabled,
    ),
  );

  const setSemantic = (patch: Partial<SemanticDraft>) =>
    setDraft({ semantic: { ...draft.semantic, ...patch } });

  const search = useMutation({
    mutationFn: async () => {
      const from = Number(draft.from);
      const to = Number(draft.to);
      if (!Number.isInteger(from) || !Number.isInteger(to)) {
        throw new Error("Enter numeric source and target vertex ids.");
      }
      // The semantic block is data, not code: build+validate it here so an invalid block
      // (empty vector, DotProduct cost, text without the provider) is a clear client-side
      // error rather than the server's 400/403.
      const semantic = buildSemanticSpec(draft.semantic, {
        allowCost: true,
        providerEnabled,
      });
      if (!semantic.ok) {
        throw new Error(`Semantic block: ${semantic.error}`);
      }
      return (
        (await findPaths(instance, from, to, buildPathSpecification(draft, semantic.spec))) ?? []
      );
    },
  });

  const overlay = useMutation({
    mutationFn: async (path: PathREST) => {
      // Hydrate the path's vertices + edges into the canvas, then highlight (FR-14).
      const vertexIds = new Set<number>();
      for (const el of path.pathElements) {
        vertexIds.add(el.sourceVertexId);
        vertexIds.add(el.targetVertexId);
      }
      const vertices = (
        await Promise.all(
          [...vertexIds].map((id) => getGraphElement(instance, id).catch(() => null)),
        )
      ).filter((v): v is VertexREST => v !== null);
      const edges = path.pathElements.map((el) => ({
        id: el.edgeId,
        creationDate: "",
        modificationDate: "",
        sourceVertex: el.sourceVertexId,
        targetVertex: el.targetVertexId,
        edgePropertyId: el.edgePropertyId ?? null,
        label: null,
      }));
      mergeIntoCanvas(vertices, edges);
      setPathOverlay(path);
      navigate({ to: "/canvas" });
    },
  });

  const slotContext = `Path finder · ${draft.from || "?"} → ${draft.to || "?"}`;

  return (
    <div className="mx-auto max-w-4xl space-y-4">
      <section className="panel">
        <div className="panel-title">Path query</div>
        <form
          className="space-y-3 p-3"
          onSubmit={(e) => {
            e.preventDefault();
            search.mutate();
          }}
        >
          <div className="flex flex-wrap items-end gap-3">
            <Field helpKey="pathFrom" label="from vertex id" htmlFor="path-from">
              <input
                id="path-from"
                data-testid="path-from"
                className="input w-28"
                value={draft.from}
                onChange={(e) => setDraft({ from: e.target.value })}
              />
            </Field>
            <Field helpKey="pathTo" label="to vertex id" htmlFor="path-to">
              <input
                id="path-to"
                data-testid="path-to"
                className="input w-28"
                value={draft.to}
                onChange={(e) => setDraft({ to: e.target.value })}
              />
            </Field>
            <Field helpKey="pathAlgorithm" label="algorithm" htmlFor="path-algo">
              <select
                id="path-algo"
                data-testid="path-algo"
                className="input w-auto"
                value={draft.algorithm}
                onChange={(e) => {
                  const algorithm = e.target.value as "BLS" | "DIJKSTRA";
                  // BLS ignores costs; drop a stale costBySimilarity so it is never sent
                  // (and never owns the vertex-cost slot) under a hop-count search.
                  setDraft(
                    algorithm === "BLS"
                      ? { algorithm, semantic: { ...draft.semantic, costBySimilarity: false } }
                      : { algorithm },
                  );
                }}
              >
                <option value="BLS">BLS (hop count)</option>
                <option value="DIJKSTRA">Dijkstra (weighted)</option>
              </select>
            </Field>
            <Field helpKey="pathMaxDepth" label="maxDepth" htmlFor="path-depth">
              <input
                id="path-depth"
                className="input w-20"
                type="number"
                min={0}
                value={draft.maxDepth}
                onChange={(e) => setDraft({ maxDepth: Number(e.target.value) })}
              />
            </Field>
            <Field
              helpKey="pathMaxResults"
              label={`maxResults${draft.algorithm === "DIJKSTRA" ? " (K)" : ""}`}
              htmlFor="path-results"
            >
              <input
                id="path-results"
                className="input w-20"
                type="number"
                min={1}
                value={draft.maxResults}
                onChange={(e) => setDraft({ maxResults: Number(e.target.value) })}
              />
            </Field>
            {draft.algorithm === "DIJKSTRA" && (
              <Field helpKey="pathMaxWeight" label="maxPathWeight" htmlFor="path-weight">
                <input
                  id="path-weight"
                  className="input w-28"
                  type="number"
                  value={draft.maxPathWeight === Number.MAX_VALUE ? "" : draft.maxPathWeight}
                  placeholder="∞"
                  onChange={(e) =>
                    setDraft({
                      maxPathWeight:
                        e.target.value === "" ? Number.MAX_VALUE : Number(e.target.value),
                    })
                  }
                />
              </Field>
            )}
            <button
              type="submit"
              className="btn btn-accent"
              data-testid="path-run"
              disabled={
                search.isPending ||
                (draft.filterSource === "stored" && !draft.storedQuery) ||
                !buildSemanticSpec(draft.semantic, { allowCost: true, providerEnabled }).ok
              }
              title={
                draft.filterSource === "stored" && !draft.storedQuery
                  ? "pick a stored query first"
                  : undefined
              }
            >
              {search.isPending ? "Searching…" : "Find paths"}
            </button>
          </div>

          <p className="text-fg-faint text-[11px]">
            BLS finds hop-count-shortest paths and <em>ignores</em> the cost fragments and
            maxPathWeight (totalWeight stays 0). Dijkstra honours costs; its maxResults is
            the K in K-shortest-paths.
          </p>

          <FilterSourceToggle
            value={draft.filterSource}
            onChange={(filterSource) => setDraft({ filterSource })}
          />

          {/* Semantic scoring composes with BOTH inline fragments and a stored path query
              (it only supplies the query vector), so it is always available here. */}
          <SemanticBlockEditor
            draft={draft.semantic}
            onChange={setSemantic}
            allowCost
            costDisabledReason={
              draft.algorithm === "BLS" ? "BLS ignores costs — use DIJKSTRA" : undefined
            }
            providerEnabled={providerEnabled}
            embeddingNames={suggestions.embeddingNames}
            idPrefix="path"
          />

          {draft.filterSource === "stored" && (
            <StoredQueryPicker
              instance={instance}
              kind="Path"
              value={draft.storedQuery}
              onChange={(storedQuery) => setDraft({ storedQuery })}
            />
          )}

          {draft.filterSource === "inline" && (
            <button
              type="button"
              className="btn"
              data-testid="toggle-advanced"
              onClick={() => setShowAdvanced((s) => !s)}
            >
              {showAdvanced ? "Hide" : "Show"} advanced filters &amp; costs
            </button>
          )}

          {draft.filterSource === "inline" && showAdvanced && (
            <div className="space-y-2" data-testid="advanced-slots">
              <DelegateSlot
                instance={instance}
                delegateKind="VertexFilter"
                label="filter.vertexFilter"
                contextLabel={slotContext}
                value={draft.vertexFilter}
                onChange={(fragment) => setDraft({ vertexFilter: fragment })}
                disabled={semanticOwnsVertexFilter(draft.semantic)}
                disabledReason="owned by semantic minScore — clear it to write a fragment"
              />
              <DelegateSlot
                instance={instance}
                delegateKind="EdgeFilter"
                label="filter.edgeFilter"
                contextLabel={slotContext}
                value={draft.edgeFilter}
                onChange={(fragment) => setDraft({ edgeFilter: fragment })}
              />
              <DelegateSlot
                instance={instance}
                delegateKind="EdgePropertyFilter"
                label="filter.edgePropertyFilter"
                contextLabel={slotContext}
                value={draft.edgePropertyFilter}
                onChange={(fragment) => setDraft({ edgePropertyFilter: fragment })}
              />
              <DelegateSlot
                instance={instance}
                delegateKind="VertexCost"
                label="cost.vertexCost"
                contextLabel={slotContext}
                value={draft.vertexCost}
                onChange={(fragment) => setDraft({ vertexCost: fragment })}
                disabled={semanticOwnsVertexCost(draft.semantic)}
                disabledReason="owned by semantic costBySimilarity — clear it to write a fragment"
              />
              <DelegateSlot
                instance={instance}
                delegateKind="EdgeCost"
                label="cost.edgeCost"
                contextLabel={slotContext}
                value={draft.edgeCost}
                onChange={(fragment) => setDraft({ edgeCost: fragment })}
              />
              <SaveAsStoredQuery
                instance={instance}
                kind="Path"
                buildBlock={() => pathBlockFromDraft(draft)}
                disabled={!hasAnyPathFragment(draft)}
                disabledReason="commit at least one fragment first"
                onSaved={(name) =>
                  setDraft({ filterSource: "stored", storedQuery: name })
                }
              />
            </div>
          )}
        </form>
        {search.isError && (
          <div className="space-y-2 px-3 pb-3">
            <ErrorBox error={search.error} />
            {search.error instanceof ApiError &&
              search.error.status === 403 &&
              draft.filterSource === "inline" && (
                <p className="text-fg-dim text-[12px]" data-testid="path-403-hint">
                  Dynamic code execution is off on this instance — switch filters to a
                  stored query instead.
                </p>
              )}
          </div>
        )}
      </section>

      {search.isSuccess && (
        <section className="panel">
          <div className="panel-title">
            results — {search.data.length} path(s)
          </div>
          {search.data.length === 0 ? (
            <div className="text-fg-dim p-3 text-[12px]" data-testid="no-paths">
              No paths found. (Fragments were validated before submission, so this is a
              real “no paths”, not a swallowed compile error.)
            </div>
          ) : (
            <div className="space-y-2 p-3">
              {search.data.map((path, index) => (
                <div key={index} className="panel p-2">
                  <div className="flex items-center gap-2 text-[12px]">
                    <span
                      className="text-fg-dim"
                      title={
                        draft.semantic.enabled && draft.semantic.costBySimilarity
                          ? draft.semantic.metric === "L2"
                            ? "each vertex cost is its L2 distance to the query vector"
                            : "each vertex cost is 1 − its cosine similarity to the query vector"
                          : undefined
                      }
                    >
                      #{index + 1} · {path.pathElements.length} hop(s) · totalWeight{" "}
                      <span data-testid={`path-weight-${index}`}>{path.totalWeight}</span>
                    </span>
                    {draft.algorithm === "BLS" && path.totalWeight === 0 && (
                      <span className="text-fg-faint">(BLS ignores costs)</span>
                    )}
                    <button
                      type="button"
                      className="btn btn-accent ml-auto"
                      data-testid={`path-overlay-${index}`}
                      onClick={() => overlay.mutate(path)}
                    >
                      Overlay on canvas
                    </button>
                  </div>
                  <ol className="text-fg-dim mt-1 text-[11px]">
                    {path.pathElements.map((el, i) => (
                      <li key={i}>
                        {el.sourceVertexId} —{el.edgePropertyId ?? "?"} (edge {el.edgeId},
                        w={el.weight})→ {el.targetVertexId}
                      </li>
                    ))}
                  </ol>
                </div>
              ))}
            </div>
          )}
        </section>
      )}
    </div>
  );
}
