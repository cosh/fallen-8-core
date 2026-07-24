import { useState } from "react";
import { useMutation, useQueryClient } from "@tanstack/react-query";
import { useActiveInstance } from "../instances/registry";
import { generateGraph, runBenchmark } from "../api/endpoints";
import { invalidateInstanceQueries } from "../api/queries";
import type { BenchmarkResult } from "../api/types";
import { useStatus } from "../state/status";
import { ErrorBox } from "../components/ErrorBox";
import { Field } from "../components/Field";
import { Stat } from "../components/Stat";
import { Truncated } from "../components/Truncated";
import { DISPLAY_CAP } from "../lib/truncate";
import { formatCompact, formatExact } from "../lib/format";

/**
 * Benchmark workspace (feature sample-graphs): graph generation + the traversal
 * benchmark, moved out of the Dashboard playground into their own growing tab. The
 * benchmark times passes over the edges /generate creates (edge property "A") — it is a
 * generated-graph benchmark, not a measurement of whatever graph happens to be loaded.
 */

type Distribution = "uniform" | "preferential";

const GENERATE_PRESETS: ReadonlyArray<{
  label: string;
  nodeCount: number;
  edgesPerVertex: number;
  distribution: Distribution;
}> = [
  { label: "small", nodeCount: 200, edgesPerVertex: 5, distribution: "uniform" },
  { label: "medium", nodeCount: 10_000, edgesPerVertex: 10, distribution: "uniform" },
  // Preferential attachment so PageRank/degree on the big graph show real hubs.
  { label: "scale (~1M edges)", nodeCount: 100_000, edgesPerVertex: 10, distribution: "preferential" },
];

interface BenchmarkRun {
  at: string;
  vertexCount: number;
  edgeCount: number;
  iterations: number;
  result: BenchmarkResult;
}

export function BenchmarkScreen() {
  const instance = useActiveInstance()!;
  const queryClient = useQueryClient();
  const status = useStatus(instance);
  const [nodeCount, setNodeCount] = useState("200");
  const [edgesPerVertex, setEdgesPerVertex] = useState("5");
  const [distribution, setDistribution] = useState<Distribution>("uniform");
  const [iterations, setIterations] = useState("1000");
  const [generateMessage, setGenerateMessage] = useState<string | null>(null);
  const [history, setHistory] = useState<BenchmarkRun[]>([]);

  const generate = useMutation({
    mutationFn: () =>
      generateGraph(
        instance,
        Number(nodeCount) || 200,
        Number(edgesPerVertex) || 5,
        distribution,
      ),
    onSuccess: (serverMessage) => {
      setGenerateMessage(serverMessage ?? "Graph generated.");
      invalidateInstanceQueries(queryClient, instance.id);
    },
  });

  const benchmark = useMutation({
    mutationFn: () => runBenchmark(instance, Number(iterations) || 1000),
    onSuccess: (result) => {
      if (!result) return;
      setHistory((runs) => [
        {
          at: new Date().toLocaleTimeString(),
          vertexCount: status.data?.vertexCount ?? 0,
          edgeCount: status.data?.edgeCount ?? 0,
          iterations: Number(iterations) || 1000,
          result,
        },
        ...runs,
      ]);
    },
  });

  return (
    <div className="mx-auto max-w-5xl space-y-4">
      <div className="flex items-center gap-2">
        <h1 className="text-fg flex min-w-0 items-baseline gap-1 text-sm font-bold tracking-wider uppercase">
          <span className="shrink-0">Benchmark —</span>
          <Truncated text={instance.name} max={DISPLAY_CAP.name} />
        </h1>
        {status.data && (
          <span className="text-fg-faint ml-auto text-[11px]">
            current graph: {status.data.vertexCount.toLocaleString()} vertices ·{" "}
            {status.data.edgeCount.toLocaleString()} edges
          </span>
        )}
      </div>

      <section className="panel">
        <div className="panel-title">Graph generation</div>
        <div className="space-y-3 p-3">
          <div className="flex flex-wrap items-end gap-2">
            <Field helpKey="benchNodeCount" label="vertices" htmlFor="bench-nodes">
              <input
                id="bench-nodes"
                className="input w-28"
                inputMode="numeric"
                value={nodeCount}
                onChange={(e) => setNodeCount(e.target.value)}
              />
            </Field>
            <Field
              helpKey="benchEdgesPerVertex"
              label="edges / vertex"
              htmlFor="bench-edges"
            >
              <input
                id="bench-edges"
                className="input w-24"
                inputMode="numeric"
                value={edgesPerVertex}
                onChange={(e) => setEdgesPerVertex(e.target.value)}
              />
            </Field>
            <Field helpKey="benchDistribution" label="distribution" htmlFor="bench-distribution">
              <select
                id="bench-distribution"
                className="input w-32"
                value={distribution}
                onChange={(e) => setDistribution(e.target.value as Distribution)}
              >
                <option value="uniform">uniform</option>
                <option value="preferential">preferential</option>
              </select>
            </Field>
            <button
              type="button"
              className="btn btn-accent"
              data-testid="generate-sample"
              disabled={generate.isPending}
              onClick={() => generate.mutate()}
            >
              {generate.isPending ? "Generating…" : "Generate"}
            </button>
            <div className="ml-4 flex items-end gap-1">
              {GENERATE_PRESETS.map((preset) => (
                <button
                  key={preset.label}
                  type="button"
                  className="btn text-[11px]"
                  data-testid={`generate-preset-${preset.nodeCount}`}
                  onClick={() => {
                    setNodeCount(String(preset.nodeCount));
                    setEdgesPerVertex(String(preset.edgesPerVertex));
                    setDistribution(preset.distribution);
                  }}
                >
                  {preset.label}
                </button>
              ))}
            </div>
          </div>
          <p className="text-fg-faint text-[11px]">
            Adds vertices with random out-edges ON TOP of the current graph (no wipe).
            The benchmark below traverses exactly these generated edges.
          </p>
          {generateMessage && (
            <div className="text-accent text-[12px]" data-testid="generate-message">
              {generateMessage}
            </div>
          )}
          {generate.isError && <ErrorBox error={generate.error} />}
        </div>
      </section>

      <section className="panel">
        <div className="panel-title">Edge-traversal throughput</div>
        <div className="space-y-3 p-3">
          <div className="flex items-end gap-2">
            <Field helpKey="benchIterations" label="iterations" htmlFor="bench-iterations">
              <input
                id="bench-iterations"
                className="input w-28"
                inputMode="numeric"
                value={iterations}
                onChange={(e) => setIterations(e.target.value)}
              />
            </Field>
            <button
              type="button"
              className="btn btn-accent"
              data-testid="run-benchmark"
              disabled={benchmark.isPending}
              onClick={() => benchmark.mutate()}
            >
              {benchmark.isPending ? "Running…" : "Run benchmark"}
            </button>
          </div>

          {benchmark.data && (
            <div
              className="border-line grid grid-cols-2 gap-3 border-t pt-3 md:grid-cols-4"
              data-testid="benchmark-result"
            >
              <Stat
                label="edges per run"
                value={formatExact(benchmark.data.edgesTraversed)}
              />
              <Stat label="avg tps" value={formatCompact(benchmark.data.averageTps)} />
              <Stat label="median tps" value={formatCompact(benchmark.data.medianTps)} />
              <Stat
                label="stddev tps"
                value={formatCompact(benchmark.data.standardDeviationTps)}
              />
              <p className="text-fg-faint col-span-full text-[11px]">
                {benchmark.data.iterations} iterations · exact average{" "}
                {formatExact(benchmark.data.averageTps)} TPS
              </p>
            </div>
          )}
          {benchmark.isError && <ErrorBox error={benchmark.error} />}
        </div>
      </section>

      {history.length > 0 && (
        <section className="panel">
          <div className="panel-title">Run history (this session)</div>
          <table className="w-full text-[12px]" data-testid="benchmark-history">
            <thead>
              <tr className="text-fg-faint text-left text-[10px] tracking-widest uppercase">
                <th className="table-cell">at</th>
                <th className="table-cell">graph</th>
                <th className="table-cell">iterations</th>
                <th className="table-cell">edges / run</th>
                <th className="table-cell">avg tps</th>
                <th className="table-cell">median tps</th>
                <th className="table-cell">stddev</th>
              </tr>
            </thead>
            <tbody>
              {history.map((run, index) => (
                <tr key={`${run.at}-${index}`} className="text-fg-dim">
                  <td className="table-cell">{run.at}</td>
                  <td className="table-cell">
                    {run.vertexCount.toLocaleString()}V / {run.edgeCount.toLocaleString()}E
                  </td>
                  <td className="table-cell">{run.iterations.toLocaleString()}</td>
                  <td className="table-cell">{formatExact(run.result.edgesTraversed)}</td>
                  <td className="table-cell">{formatCompact(run.result.averageTps)}</td>
                  <td className="table-cell">{formatCompact(run.result.medianTps)}</td>
                  <td className="table-cell">
                    {formatCompact(run.result.standardDeviationTps)}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </section>
      )}
    </div>
  );
}
