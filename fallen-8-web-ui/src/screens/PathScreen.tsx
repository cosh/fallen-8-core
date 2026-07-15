import { useState } from "react";
import { useMutation } from "@tanstack/react-query";
import { useNavigate } from "@tanstack/react-router";
import { useActiveInstance } from "../instances/registry";
import { findPaths, getGraphElement } from "../api/endpoints";
import type { PathREST, PathSpecification, VertexREST } from "../api/types";
import { getInstanceStore } from "../state/instanceStore";
import { DelegateSlot } from "../delegate/DelegateSlot";
import { ErrorBox } from "../components/ErrorBox";

/**
 * Path finder (FR-12/13/14): BLS vs Dijkstra with the explainer, defaults pre-filled,
 * the five delegate slots as the advanced tier (all optional, empty by default), results
 * as ordered element lists + canvas overlay. Fragments were already validated in the
 * editor - /path swallows compile errors, so an empty result here is "no paths found",
 * never an error (FR-13).
 */
export function PathScreen() {
  const instance = useActiveInstance()!;
  const store = getInstanceStore(instance.id);
  const draft = store((s) => s.pathDraft);
  const setDraft = store((s) => s.setPathDraft);
  const setPathOverlay = store((s) => s.setPathOverlay);
  const mergeIntoCanvas = store((s) => s.mergeIntoCanvas);
  const navigate = useNavigate();
  const [showAdvanced, setShowAdvanced] = useState(
    Boolean(
      draft.vertexFilter ||
        draft.edgeFilter ||
        draft.edgePropertyFilter ||
        draft.vertexCost ||
        draft.edgeCost,
    ),
  );

  const search = useMutation({
    mutationFn: async () => {
      const from = Number(draft.from);
      const to = Number(draft.to);
      if (!Number.isInteger(from) || !Number.isInteger(to)) {
        throw new Error("Enter numeric source and target vertex ids.");
      }
      const spec: PathSpecification = {
        pathAlgorithmName: draft.algorithm,
        maxDepth: draft.maxDepth,
        maxResults: draft.maxResults,
        maxPathWeight: draft.maxPathWeight,
        filter: {
          vertexFilter: draft.vertexFilter || undefined,
          edgeFilter: draft.edgeFilter || undefined,
          edgePropertyFilter: draft.edgePropertyFilter || undefined,
        },
        cost: {
          vertexCost: draft.vertexCost || undefined,
          edgeCost: draft.edgeCost || undefined,
        },
      };
      return (await findPaths(instance, from, to, spec)) ?? [];
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
            <div>
              <label className="label" htmlFor="path-from">
                from vertex id
              </label>
              <input
                id="path-from"
                data-testid="path-from"
                className="input w-28"
                value={draft.from}
                onChange={(e) => setDraft({ from: e.target.value })}
              />
            </div>
            <div>
              <label className="label" htmlFor="path-to">
                to vertex id
              </label>
              <input
                id="path-to"
                data-testid="path-to"
                className="input w-28"
                value={draft.to}
                onChange={(e) => setDraft({ to: e.target.value })}
              />
            </div>
            <div>
              <label className="label" htmlFor="path-algo">
                algorithm
              </label>
              <select
                id="path-algo"
                data-testid="path-algo"
                className="input w-auto"
                value={draft.algorithm}
                onChange={(e) =>
                  setDraft({ algorithm: e.target.value as "BLS" | "DIJKSTRA" })
                }
              >
                <option value="BLS">BLS (hop count)</option>
                <option value="DIJKSTRA">Dijkstra (weighted)</option>
              </select>
            </div>
            <div>
              <label className="label" htmlFor="path-depth">
                maxDepth
              </label>
              <input
                id="path-depth"
                className="input w-20"
                type="number"
                min={0}
                value={draft.maxDepth}
                onChange={(e) => setDraft({ maxDepth: Number(e.target.value) })}
              />
            </div>
            <div>
              <label className="label" htmlFor="path-results">
                maxResults{draft.algorithm === "DIJKSTRA" ? " (K)" : ""}
              </label>
              <input
                id="path-results"
                className="input w-20"
                type="number"
                min={1}
                value={draft.maxResults}
                onChange={(e) => setDraft({ maxResults: Number(e.target.value) })}
              />
            </div>
            {draft.algorithm === "DIJKSTRA" && (
              <div>
                <label className="label" htmlFor="path-weight">
                  maxPathWeight
                </label>
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
              </div>
            )}
            <button
              type="submit"
              className="btn btn-accent"
              data-testid="path-run"
              disabled={search.isPending}
            >
              {search.isPending ? "Searching…" : "Find paths"}
            </button>
          </div>

          <p className="text-fg-faint text-[11px]">
            BLS finds hop-count-shortest paths and <em>ignores</em> the cost fragments and
            maxPathWeight (totalWeight stays 0). Dijkstra honours costs; its maxResults is
            the K in K-shortest-paths.
          </p>

          <button
            type="button"
            className="btn"
            data-testid="toggle-advanced"
            onClick={() => setShowAdvanced((s) => !s)}
          >
            {showAdvanced ? "Hide" : "Show"} advanced filters &amp; costs
          </button>

          {showAdvanced && (
            <div className="space-y-2" data-testid="advanced-slots">
              <DelegateSlot
                instance={instance}
                delegateKind="VertexFilter"
                label="filter.vertexFilter"
                contextLabel={slotContext}
                value={draft.vertexFilter}
                onChange={(fragment) => setDraft({ vertexFilter: fragment })}
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
              />
              <DelegateSlot
                instance={instance}
                delegateKind="EdgeCost"
                label="cost.edgeCost"
                contextLabel={slotContext}
                value={draft.edgeCost}
                onChange={(fragment) => setDraft({ edgeCost: fragment })}
              />
            </div>
          )}
        </form>
        {search.isError && (
          <div className="px-3 pb-3">
            <ErrorBox error={search.error} />
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
                    <span className="text-fg-dim">
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
