import { useState } from "react";
import { useMutation, useQuery } from "@tanstack/react-query";
import { useInstanceStore } from "../instances/registry";
import { describeEndpoint } from "../instances/types";
import { shapeSuggestions, useGraphShape } from "../state/graphShape";
import {
  getPartitionMembers,
  listAnalyticsAlgorithms,
  runAnalytics,
} from "../api/endpoints";
import { ApiError } from "../api/client";
import type {
  AnalyticsSpecification,
  EdgeREST,
  VertexREST,
} from "../api/types";
import { hydrateElements, isEdge, type HydrationProgress } from "../lib/hydrate";
import { Stat } from "./Stat";
import { ElementTable } from "./ElementTable";
import { ConfirmDialog } from "./ConfirmDialog";
import { ErrorBox } from "./ErrorBox";
import { Field } from "./Field";
import { help } from "../lib/fieldHelp";

function describeRunError(error: unknown): string | null {
  if (error instanceof ApiError) {
    if (error.status === 429) {
      return "A run is already in progress on this instance — runs are one-shot and serialized; retry when it finishes.";
    }
    if (error.status === 408) {
      return "Budget exhausted before one full pass — raise the time budget or scope the run down (vertex label / edge property).";
    }
  }
  return null;
}

export function AnalyticsRunner() {
  const { instance, store } = useInstanceStore();
  const mergeIntoCanvas = store((s) => s.mergeIntoCanvas);
  const addResultSet = store((s) => s.addResultSet);
  const suggestions = shapeSuggestions(useGraphShape(instance).data);

  const algorithms = useQuery({
    queryKey: [instance.id, "analytics-algorithms"],
    queryFn: ({ signal }) => listAnalyticsAlgorithms(instance, signal),
  });
  const names = Object.keys(algorithms.data ?? {}).sort();

  const [algorithm, setAlgorithm] = useState("");
  const [vertexLabel, setVertexLabel] = useState("");
  const [edgePropertyId, setEdgePropertyId] = useState("");
  const [direction, setDirection] = useState("");
  const [maxResults, setMaxResults] = useState("100");
  const [maxIterations, setMaxIterations] = useState("");
  const [timeBudget, setTimeBudget] = useState("");
  const [damping, setDamping] = useState("");
  const [epsilon, setEpsilon] = useState("");
  const [showWriteBack, setShowWriteBack] = useState(false);
  const [writeBack, setWriteBack] = useState(false);
  const [writeBackKey, setWriteBackKey] = useState("");
  const [confirming, setConfirming] = useState(false);

  const [elements, setElements] = useState<(VertexREST | EdgeREST)[]>([]);
  const [scores, setScores] = useState<Map<number, number> | undefined>(undefined);
  const [progress, setProgress] = useState<HydrationProgress | null>(null);
  const [memberView, setMemberView] = useState<{
    partitionId: number;
    size: number;
    nextOffset: number;
  } | null>(null);

  const isPageRank = algorithm.toUpperCase().includes("PAGERANK");

  const buildSpec = (offset?: number): AnalyticsSpecification => ({
    vertexLabel: vertexLabel.trim() || undefined,
    edgePropertyId: edgePropertyId.trim() || undefined,
    direction: (direction || undefined) as AnalyticsSpecification["direction"],
    maxResults: Number(maxResults) > 0 ? Number(maxResults) : undefined,
    maxIterations: maxIterations ? Number(maxIterations) : undefined,
    timeBudgetSeconds: timeBudget ? Number(timeBudget) : undefined,
    epsilon: isPageRank && epsilon ? Number(epsilon) : undefined,
    parameters:
      isPageRank && damping ? { DampingFactor: Number(damping) } : undefined,
    offset,
    writeBack: writeBack || undefined,
    writeBackPropertyKey:
      writeBack && writeBackKey.trim() ? writeBackKey.trim() : undefined,
  });

  const run = useMutation({
    mutationFn: async () => {
      setElements([]);
      setScores(undefined);
      setMemberView(null);
      const result = await runAnalytics(instance, algorithm, buildSpec());
      if (result?.results?.length) {
        const ids = result.results.map((r) => r.graphElementId);
        addResultSet(`${algorithm} top-${ids.length}`, ids);
        const hydrated = await hydrateElements(instance, ids, {
          onProgress: setProgress,
        });
        setElements(hydrated.elements);
        setScores(new Map(result.results.map((r) => [r.graphElementId, r.score])));
      }
      return result;
    },
    onSettled: () => setProgress(null),
  });

  const members = useMutation({
    mutationFn: async (input: { partitionId: number; offset: number }) => {
      const page = await getPartitionMembers(
        instance,
        algorithm,
        input.partitionId,
        buildSpec(input.offset),
      );
      const ids = page?.members ?? [];
      const hydrated = await hydrateElements(instance, ids, {
        onProgress: setProgress,
      });
      return { page, hydrated };
    },
    onSuccess: ({ page, hydrated }, input) => {
      if (!page) return;
      setScores(undefined);
      setElements((previous) =>
        input.offset > 0 ? [...previous, ...hydrated.elements] : hydrated.elements,
      );
      setMemberView({
        partitionId: page.partitionId,
        size: page.size,
        nextOffset: page.offset + (page.members?.length ?? 0),
      });
    },
    onSettled: () => setProgress(null),
  });

  const result = run.data;
  const runHint = describeRunError(run.error);

  return (
    <>
      <section className="panel">
        <div className="panel-title">Run</div>
        <form
          className="space-y-3 p-3"
          onSubmit={(e) => {
            e.preventDefault();
            if (writeBack) setConfirming(true);
            else run.mutate();
          }}
        >
          <div className="flex flex-wrap items-end gap-3">
            <Field helpKey="analyticsAlgorithm" label="algorithm" htmlFor="algo-name">
              <select
                id="algo-name"
                data-testid="algo-name"
                className="input w-56"
                value={algorithm}
                onChange={(e) => setAlgorithm(e.target.value)}
              >
                <option value="">— pick an algorithm —</option>
                {names.map((name) => (
                  <option key={name} value={name}>
                    {name}
                  </option>
                ))}
              </select>
            </Field>
            <Field
              helpKey="analyticsVertexLabel"
              label="vertex label"
              htmlFor="algo-vertex-label"
            >
              <input
                id="algo-vertex-label"
                className="input w-36"
                list="analytics-vertex-labels"
                value={vertexLabel}
                onChange={(e) => setVertexLabel(e.target.value)}
                placeholder="empty = whole graph"
              />
            </Field>
            <Field
              helpKey="analyticsEdgeProperty"
              label="edge property"
              htmlFor="algo-edge-property"
            >
              <input
                id="algo-edge-property"
                className="input w-32"
                value={edgePropertyId}
                onChange={(e) => setEdgePropertyId(e.target.value)}
                placeholder="empty = all edges"
              />
            </Field>
            <Field helpKey="analyticsDirection" label="direction" htmlFor="algo-direction">
              <select
                id="algo-direction"
                className="input w-auto"
                value={direction}
                onChange={(e) => setDirection(e.target.value)}
              >
                <option value="">algorithm default</option>
                <option value="in">in</option>
                <option value="out">out</option>
                <option value="both">both</option>
              </select>
            </Field>
            <Field
              helpKey="analyticsMaxResults"
              label="max results"
              htmlFor="algo-max-results"
            >
              <input
                id="algo-max-results"
                className="input w-24"
                type="number"
                min={1}
                max={10000}
                value={maxResults}
                onChange={(e) => setMaxResults(e.target.value)}
              />
            </Field>
            <Field
              helpKey="analyticsMaxIterations"
              label="max iterations"
              htmlFor="algo-max-iterations"
            >
              <input
                id="algo-max-iterations"
                className="input w-24"
                type="number"
                min={1}
                max={10000}
                value={maxIterations}
                onChange={(e) => setMaxIterations(e.target.value)}
                placeholder="default"
              />
            </Field>
            <Field helpKey="analyticsTimeBudget" label="time budget s" htmlFor="algo-budget">
              <input
                id="algo-budget"
                className="input w-24"
                type="number"
                min={1}
                value={timeBudget}
                onChange={(e) => setTimeBudget(e.target.value)}
                placeholder="default 30"
              />
            </Field>
            {isPageRank && (
              <>
                <Field
                  helpKey="analyticsDamping"
                  label="DampingFactor"
                  htmlFor="algo-damping"
                >
                  <input
                    id="algo-damping"
                    className="input w-24"
                    type="number"
                    step="0.01"
                    min={0}
                    max={1}
                    value={damping}
                    onChange={(e) => setDamping(e.target.value)}
                    placeholder="0.85"
                  />
                </Field>
                <Field helpKey="analyticsEpsilon" label="epsilon" htmlFor="algo-epsilon">
                  <input
                    id="algo-epsilon"
                    className="input w-24"
                    type="number"
                    step="any"
                    min={0}
                    value={epsilon}
                    onChange={(e) => setEpsilon(e.target.value)}
                    placeholder="1e-6"
                  />
                </Field>
              </>
            )}
            <button
              type="submit"
              className="btn btn-accent"
              data-testid="algo-run"
              disabled={!algorithm || run.isPending}
            >
              {run.isPending ? "Running…" : "Run"}
            </button>
          </div>

          {algorithm && algorithms.data?.[algorithm] && (
            <p className="text-fg-faint text-[11px]" data-testid="algo-description">
              {algorithms.data[algorithm]}
            </p>
          )}

          <button
            type="button"
            className="btn"
            data-testid="toggle-write-back"
            onClick={() => setShowWriteBack((s) => !s)}
          >
            {showWriteBack ? "Hide" : "Show"} write-back
          </button>
          {showWriteBack && (
            <div className="flex flex-wrap items-end gap-3">
              <label
                className="text-fg-dim label-help flex items-center gap-1 pb-1 text-[12px]"
                title={help("analyticsWriteBack")}
              >
                <input
                  type="checkbox"
                  data-testid="write-back-checkbox"
                  checked={writeBack}
                  onChange={(e) => setWriteBack(e.target.checked)}
                />
                write back to properties
              </label>
              <Field helpKey="analyticsWriteBackKey" label="property key" htmlFor="write-back-key">
                <input
                  id="write-back-key"
                  className="input w-56"
                  value={writeBackKey}
                  onChange={(e) => setWriteBackKey(e.target.value)}
                  placeholder={`analytics.${algorithm.toLowerCase() || "…"}`}
                />
              </Field>
              <p className="text-fg-faint basis-full text-[11px]">
                re-runs overwrite · snapshot-durable only (WAL replay drops them — re-run
                to restore).
              </p>
            </div>
          )}

          {progress && (
            <div className="text-fg-dim text-[12px]">
              hydrating {progress.done}/{progress.total}…
            </div>
          )}
        </form>
        {(run.isError || members.isError) && (
          <div className="space-y-2 px-3 pb-3">
            <ErrorBox error={run.error ?? members.error} />
            {runHint && (
              <p className="text-fg-dim text-[12px]" data-testid="run-hint">
                {runHint}
              </p>
            )}
          </div>
        )}
      </section>

      {result && (
        <section className="panel" data-testid="analytics-result">
          <div className="panel-title">
            Result — {result.algorithm}
            <span
              className={`normal-case ${result.converged ? "text-accent" : "text-warn"}`}
            >
              {result.converged ? "converged" : "not converged"}
            </span>
            <span className="text-fg-faint normal-case">
              iterations {result.iterationsRun} · {result.elapsedMs.toFixed(0)} ms ·
              vertices {result.vertexCount.toLocaleString()}
            </span>
            {result.budgetExhausted && (
              <span className="text-warn normal-case">budget exhausted — partial</span>
            )}
          </div>
          <div className="space-y-3 p-3">
            {result.statistics && Object.keys(result.statistics).length > 0 && (
              <div className="grid grid-cols-2 gap-3 md:grid-cols-4">
                {Object.entries(result.statistics).map(([key, value]) => (
                  <Stat
                    key={key}
                    label={key}
                    value={Number.isInteger(value) ? value.toLocaleString() : value.toFixed(4)}
                  />
                ))}
              </div>
            )}

            {result.writeBack && (
              <p className="text-accent text-[12px]" data-testid="write-back-report">
                wrote {result.writeBack.propertyKey} on{" "}
                {result.writeBack.verticesWritten.toLocaleString()} vertices in{" "}
                {result.writeBack.chunks} chunk(s) — visible in element properties on
                Browser and Canvas detail.
              </p>
            )}

            {result.partitions && result.partitions.length > 0 && (
              <div className="panel overflow-x-auto">
                <div className="panel-title">
                  partitions — {result.partitions.length}
                  <span className="text-fg-faint normal-case">
                    members re-run the specification — exact only on a quiescent graph
                  </span>
                </div>
                <table className="w-full text-[12px]">
                  <thead>
                    <tr className="text-fg-faint">
                      <th className="table-cell">partition id</th>
                      <th className="table-cell text-right">size</th>
                      <th className="table-cell w-28" />
                    </tr>
                  </thead>
                  <tbody>
                    {result.partitions.map((partition) => (
                      <tr key={partition.partitionId}>
                        <td className="table-cell font-semibold">
                          {partition.partitionId}
                        </td>
                        <td className="table-cell text-fg-dim text-right">
                          {partition.size.toLocaleString()}
                        </td>
                        <td className="table-cell">
                          <button
                            type="button"
                            className="btn"
                            disabled={members.isPending}
                            onClick={() =>
                              members.mutate({
                                partitionId: partition.partitionId,
                                offset: 0,
                              })
                            }
                          >
                            Members…
                          </button>
                        </td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
            )}

            {(elements.length > 0 || memberView) && (
              <div className="panel">
                <div className="panel-title">
                  {memberView
                    ? `partition ${memberView.partitionId} — ${Math.min(
                        memberView.nextOffset,
                        memberView.size,
                      ).toLocaleString()} of ${memberView.size.toLocaleString()} members`
                    : `top-${elements.length} scored`}
                  <button
                    type="button"
                    className="btn btn-accent ml-auto"
                    data-testid="analytics-to-canvas"
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
                <ElementTable elements={elements} scores={scores} />
                {memberView && memberView.nextOffset < memberView.size && (
                  <div className="p-3">
                    <button
                      type="button"
                      className="btn"
                      disabled={members.isPending}
                      onClick={() =>
                        members.mutate({
                          partitionId: memberView.partitionId,
                          offset: memberView.nextOffset,
                        })
                      }
                    >
                      {members.isPending ? "Loading…" : "More members"}
                    </button>
                  </div>
                )}
              </div>
            )}
          </div>
        </section>
      )}

      <datalist id="analytics-vertex-labels">
        {suggestions.vertexLabels.map((label) => (
          <option key={label} value={label} />
        ))}
      </datalist>

      <ConfirmDialog
        open={confirming}
        title="Run with write-back"
        description={`This writes '${
          writeBackKey.trim() || `analytics.${algorithm.toLowerCase()}`
        }' onto every in-scope vertex (re-runs overwrite; snapshot-durable only — WAL replay drops analytics properties).`}
        instanceName={instance.name}
        endpoint={describeEndpoint(instance)}
        confirmLabel="Run and write back"
        onConfirm={() => {
          setConfirming(false);
          run.mutate();
        }}
        onCancel={() => setConfirming(false)}
      />
    </>
  );
}
