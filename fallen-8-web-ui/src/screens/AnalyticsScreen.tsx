import { useState } from "react";
import { useMutation, useQuery } from "@tanstack/react-query";
import { useNavigate } from "@tanstack/react-router";
import { useActiveInstance } from "../instances/registry";
import { describeEndpoint } from "../instances/types";
import { shapeSuggestions, useGraphShape } from "../state/graphShape";
import { getInstanceStore } from "../state/instanceStore";
import {
  getPartitionMembers,
  listAnalyticsAlgorithms,
  runAnalytics,
} from "../api/endpoints";
import { ApiError } from "../api/client";
import type {
  AnalyticsSpecification,
  CardinalityStatsREST,
  DegreeStatsREST,
  EdgeREST,
  GraphStatisticsREST,
  VertexREST,
} from "../api/types";
import { hydrateElements, isEdge, type HydrationProgress } from "../lib/hydrate";
import { Stat } from "../components/Stat";
import { ElementTable } from "../components/ElementTable";
import { ConfirmDialog } from "../components/ConfirmDialog";
import { ErrorBox } from "../components/ErrorBox";

/**
 * Analytics (feature studio-coverage §3/§4): understand the graph's shape, then compute
 * over it. The Graph shape panel is the ONLY caller of GET /statistics (on demand — the
 * pass is budgeted and rate-limited); its snapshot doubles as the schema cache feeding
 * identifier suggestions across the Studio (gap G-3). The runner mirrors the backend's
 * one-shot design: no history, no queueing — 429/408 are first-class outcomes.
 */
export function AnalyticsScreen() {
  return (
    <div className="mx-auto max-w-5xl space-y-4">
      <GraphShapePanel />
      <AnalyticsRunner />
    </div>
  );
}

function CardinalityColumn({
  title,
  stats,
}: {
  title: string;
  stats: CardinalityStatsREST | undefined;
}) {
  const top = stats?.top ?? [];
  return (
    <div className="panel">
      <div className="panel-title">
        {title}
        <span className="text-fg-faint normal-case">
          {stats ? `${stats.distinctTotal} distinct` : ""}
        </span>
      </div>
      <ul className="p-3 text-[12px]">
        {top.length === 0 && <li className="text-fg-faint">none</li>}
        {top.map((entry) => (
          <li
            key={entry.name ?? "—"}
            className="text-fg-dim flex justify-between gap-2"
          >
            <span className="truncate">{entry.name ?? "—"}</span>
            <span className="text-fg-faint">{entry.count.toLocaleString()}</span>
          </li>
        ))}
      </ul>
    </div>
  );
}

const DEGREE_COLUMNS = ["min", "mean", "p50", "p90", "p99", "max"] as const;

function degreeCell(stats: DegreeStatsREST, column: (typeof DEGREE_COLUMNS)[number]) {
  const value = stats[column];
  return column === "mean" ? value.toFixed(1) : value.toLocaleString();
}

function DegreeTable({ shape }: { shape: GraphStatisticsREST }) {
  const rows: [string, DegreeStatsREST][] = [
    ["in", shape.inDegree],
    ["out", shape.outDegree],
    ["total", shape.totalDegree],
  ];
  return (
    <div className="panel overflow-x-auto">
      <div className="panel-title">degrees</div>
      <table className="w-full font-mono text-[12px]">
        <thead>
          <tr className="text-fg-faint">
            <th className="table-cell" />
            {DEGREE_COLUMNS.map((column) => (
              <th key={column} className="table-cell text-right">
                {column}
              </th>
            ))}
          </tr>
        </thead>
        <tbody>
          {rows.map(([name, stats]) => (
            <tr key={name}>
              <td className="table-cell text-fg-faint">{name}</td>
              {DEGREE_COLUMNS.map((column) => (
                <td key={column} className="table-cell text-fg-dim text-right">
                  {degreeCell(stats, column)}
                </td>
              ))}
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}

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

function AnalyticsRunner() {
  const instance = useActiveInstance()!;
  const store = getInstanceStore(instance.id);
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
            <div>
              <label className="label" htmlFor="algo-name">
                algorithm
              </label>
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
            </div>
            <div>
              <label className="label" htmlFor="algo-vertex-label">
                vertex label
              </label>
              <input
                id="algo-vertex-label"
                className="input w-36"
                list="analytics-vertex-labels"
                value={vertexLabel}
                onChange={(e) => setVertexLabel(e.target.value)}
                placeholder="empty = whole graph"
              />
            </div>
            <div>
              <label className="label" htmlFor="algo-edge-property">
                edge property
              </label>
              <input
                id="algo-edge-property"
                className="input w-32"
                value={edgePropertyId}
                onChange={(e) => setEdgePropertyId(e.target.value)}
                placeholder="empty = all edges"
              />
            </div>
            <div>
              <label className="label" htmlFor="algo-direction">
                direction
              </label>
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
            </div>
            <div>
              <label className="label" htmlFor="algo-max-results">
                max results
              </label>
              <input
                id="algo-max-results"
                className="input w-24"
                type="number"
                min={1}
                max={10000}
                value={maxResults}
                onChange={(e) => setMaxResults(e.target.value)}
              />
            </div>
            <div>
              <label className="label" htmlFor="algo-max-iterations">
                max iterations
              </label>
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
            </div>
            <div>
              <label className="label" htmlFor="algo-budget">
                time budget s
              </label>
              <input
                id="algo-budget"
                className="input w-24"
                type="number"
                min={1}
                value={timeBudget}
                onChange={(e) => setTimeBudget(e.target.value)}
                placeholder="default 30"
              />
            </div>
            {isPageRank && (
              <>
                <div>
                  <label className="label" htmlFor="algo-damping">
                    DampingFactor
                  </label>
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
                </div>
                <div>
                  <label className="label" htmlFor="algo-epsilon">
                    epsilon
                  </label>
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
                </div>
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
              <label className="text-fg-dim flex items-center gap-1 pb-1 text-[12px]">
                <input
                  type="checkbox"
                  data-testid="write-back-checkbox"
                  checked={writeBack}
                  onChange={(e) => setWriteBack(e.target.checked)}
                />
                write back to properties
              </label>
              <div>
                <label className="label" htmlFor="write-back-key">
                  property key
                </label>
                <input
                  id="write-back-key"
                  className="input w-56"
                  value={writeBackKey}
                  onChange={(e) => setWriteBackKey(e.target.value)}
                  placeholder={`analytics.${algorithm.toLowerCase() || "…"}`}
                />
              </div>
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

function GraphShapePanel() {
  const instance = useActiveInstance()!;
  const shape = useGraphShape(instance);
  const store = getInstanceStore(instance.id);
  const setScanPrefill = store((s) => s.setScanPrefill);
  const navigate = useNavigate();
  const data = shape.data;

  return (
    <section className="panel">
      <div className="panel-title">
        Graph shape
        {data && (
          <span className="text-fg-faint normal-case">
            computed in {data.computedInMs.toFixed(0)} ms
          </span>
        )}
        {data?.sampled && (
          <span
            className="text-warn normal-case"
            data-testid="shape-sampled"
            title="per-name counts and distinct totals are within-sample — multiply counts by the stride to extrapolate"
          >
            sampled 1:{data.sampleStride}
          </span>
        )}
        <button
          type="button"
          className="btn btn-accent ml-auto"
          data-testid="shape-compute"
          disabled={shape.isFetching}
          onClick={() => shape.refetch()}
        >
          {shape.isFetching ? "Computing…" : data ? "Recompute" : "Compute"}
        </button>
      </div>
      <p className="text-fg-faint px-3 pt-2 text-[11px]">
        Full O(V+E) pass, sampled above the configured element budget — computed only on
        demand. The snapshot also feeds identifier suggestions on the Query screen.
      </p>

      {shape.isError && (
        <div className="p-3">
          <ErrorBox error={shape.error} onRetry={() => shape.refetch()} />
        </div>
      )}

      {data && (
        <div className="space-y-3 p-3" data-testid="shape-result">
          <div className="grid grid-cols-2 gap-3 md:grid-cols-2">
            <Stat label="vertices" value={data.vertexCount.toLocaleString()} />
            <Stat label="edges" value={data.edgeCount.toLocaleString()} />
          </div>

          <div className="grid grid-cols-1 gap-3 md:grid-cols-3">
            <CardinalityColumn title="vertex labels" stats={data.vertexLabels} />
            <CardinalityColumn title="edge labels" stats={data.edgeLabels} />
            <CardinalityColumn title="property keys" stats={data.propertyKeys} />
          </div>

          <DegreeTable shape={data} />

          <div className="panel overflow-x-auto">
            <div className="panel-title">indices</div>
            <table className="w-full text-[12px]">
              <thead>
                <tr className="text-fg-faint">
                  <th className="table-cell">name</th>
                  <th className="table-cell">type</th>
                  <th className="table-cell text-right">keys</th>
                  <th className="table-cell text-right">values</th>
                  <th className="table-cell w-20" />
                </tr>
              </thead>
              <tbody>
                {(data.indices ?? []).map((index) => (
                  <tr key={index.name ?? "—"}>
                    <td className="table-cell font-semibold">{index.name ?? "—"}</td>
                    <td className="table-cell text-fg-dim">{index.type ?? "—"}</td>
                    <td className="table-cell text-fg-dim text-right">
                      {index.keys.toLocaleString()}
                    </td>
                    <td className="table-cell text-fg-dim text-right">
                      {index.values.toLocaleString()}
                    </td>
                    <td className="table-cell">
                      {index.name && (
                        <button
                          type="button"
                          className="btn"
                          onClick={() => {
                            setScanPrefill({ kind: "index", indexId: index.name! });
                            navigate({ to: "/query" });
                          }}
                        >
                          Scan
                        </button>
                      )}
                    </td>
                  </tr>
                ))}
                {(data.indices ?? []).length === 0 && (
                  <tr>
                    <td className="table-cell text-fg-faint" colSpan={5}>
                      no indices
                    </td>
                  </tr>
                )}
              </tbody>
            </table>
          </div>
        </div>
      )}
    </section>
  );
}
