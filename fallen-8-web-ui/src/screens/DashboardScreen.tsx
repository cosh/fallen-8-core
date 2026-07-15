import { useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useActiveInstance } from "../instances/registry";
import { describeEndpoint } from "../instances/types";
import {
  generateSampleGraph,
  getStatus,
  loadGraph,
  runBenchmark,
  saveGraph,
  tabulaRasa,
  trimGraph,
} from "../api/endpoints";
import { ErrorBox } from "../components/ErrorBox";
import { ConfirmDialog } from "../components/ConfirmDialog";
import { formatCompact, formatExact } from "../lib/format";

/**
 * Dashboard (FR-2/3/4): counts, memory, plugin lists from /status; admin actions with
 * typed confirmation for the destructive ones (load, tabula rasa) naming the instance
 * (FR-1d); demo-data playground (generate/benchmark). A 500 from save/load is a rolled
 * back transaction and renders as a real failure.
 */

function Stat({ label, value }: { label: string; value: string }) {
  return (
    <div className="panel px-3 py-2">
      <div className="text-fg-faint text-[10px] tracking-widest uppercase">{label}</div>
      <div className="text-fg mt-1 text-xl" data-testid={`stat-${label.replace(/\s/g, "-")}`}>
        {value}
      </div>
    </div>
  );
}

function PluginList({ title, plugins }: { title: string; plugins: string[] }) {
  return (
    <div className="panel">
      <div className="panel-title">{title}</div>
      <ul className="p-3 text-[12px]">
        {plugins.length === 0 && <li className="text-fg-faint">none</li>}
        {plugins.map((plugin) => (
          <li key={plugin} className="text-fg-dim">
            {plugin}
          </li>
        ))}
      </ul>
    </div>
  );
}

export function DashboardScreen() {
  const instance = useActiveInstance()!;
  const queryClient = useQueryClient();
  const [confirming, setConfirming] = useState<"tabularasa" | "load" | null>(null);
  const [loadPath, setLoadPath] = useState("");
  const [lastMessage, setLastMessage] = useState<string | null>(null);

  const status = useQuery({
    queryKey: [instance.id, "status"],
    queryFn: ({ signal }) => getStatus(instance, signal),
  });

  const refresh = () => queryClient.invalidateQueries({ queryKey: [instance.id] });

  const save = useMutation({
    mutationFn: () => saveGraph(instance),
    onSuccess: (entry) => {
      setLastMessage(
        entry
          ? `Saved to ${entry.location} — registered as save game ${entry.id}. See the Save games screen.`
          : "Saved.",
      );
      refresh();
    },
  });
  const load = useMutation({
    mutationFn: () => loadGraph(instance, loadPath),
    onSuccess: () => {
      setLastMessage(`Loaded from ${loadPath}`);
      refresh();
    },
  });
  const trim = useMutation({
    mutationFn: () => trimGraph(instance),
    onSuccess: () => setLastMessage("Trim requested."),
  });
  const erase = useMutation({
    mutationFn: () => tabulaRasa(instance),
    onSuccess: () => {
      setLastMessage("All data erased.");
      refresh();
    },
  });
  const generate = useMutation({
    mutationFn: () => generateSampleGraph(instance),
    onSuccess: () => {
      setLastMessage("Sample graph generated.");
      refresh();
    },
  });
  const benchmark = useMutation({
    mutationFn: () => runBenchmark(instance),
  });

  const failed = [save, load, trim, erase, generate].find((m) => m.isError);

  if (status.isPending) {
    return <div className="text-fg-faint">Loading status…</div>;
  }
  if (status.isError) {
    return <ErrorBox error={status.error} onRetry={() => status.refetch()} />;
  }
  const data = status.data!;

  return (
    <div className="mx-auto max-w-5xl space-y-4">
      <div className="flex items-center gap-2">
        <h1 className="text-fg text-sm font-bold tracking-wider uppercase">
          Dashboard — {instance.name}
        </h1>
        <button type="button" className="btn ml-auto" onClick={() => status.refetch()}>
          Refresh
        </button>
      </div>

      <div className="grid grid-cols-2 gap-3 md:grid-cols-4">
        <Stat label="vertices" value={data.vertexCount.toLocaleString()} />
        <Stat label="edges" value={data.edgeCount.toLocaleString()} />
        <Stat label="used memory" value={`${(data.usedMemory / 1024 / 1024).toFixed(1)} MiB`} />
        <Stat label="free memory" value={`${(data.freeMemory / 1024 / 1024).toFixed(1)} MiB`} />
      </div>

      <div className="grid grid-cols-1 gap-3 md:grid-cols-3">
        <PluginList title="Index plugins" plugins={data.availableIndexPlugins ?? []} />
        <PluginList title="Path plugins" plugins={data.availablePathPlugins ?? []} />
        <PluginList title="Service plugins" plugins={data.availableServicePlugins ?? []} />
      </div>

      <section className="panel">
        <div className="panel-title">Playground</div>
        <div className="flex gap-2 p-3">
          <button
            type="button"
            className="btn btn-accent"
            data-testid="generate-sample"
            disabled={generate.isPending}
            onClick={() => generate.mutate()}
          >
            {generate.isPending ? "Generating…" : "Generate sample graph"}
          </button>
          <button
            type="button"
            className="btn"
            data-testid="run-benchmark"
            disabled={benchmark.isPending}
            onClick={() => benchmark.mutate()}
          >
            {benchmark.isPending ? "Running…" : "Run benchmark"}
          </button>
        </div>
        {benchmark.data && (
          <div
            className="border-line grid grid-cols-2 gap-3 border-t p-3 md:grid-cols-4"
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
        {benchmark.isError && (
          <div className="px-3 pb-3">
            <ErrorBox error={benchmark.error} />
          </div>
        )}
      </section>

      <section className="panel">
        <div className="panel-title">Administration</div>
        <div className="space-y-3 p-3">
          <div className="flex flex-wrap items-end gap-2">
            <button
              type="button"
              className="btn"
              disabled={save.isPending}
              onClick={() => save.mutate()}
            >
              {save.isPending ? "Saving…" : "Save"}
            </button>
            <button
              type="button"
              className="btn"
              disabled={trim.isPending}
              onClick={() => trim.mutate()}
            >
              Trim
            </button>
            <div className="ml-4 flex items-end gap-2">
              <div>
                <label className="label" htmlFor="load-path">
                  load path
                </label>
                <input
                  id="load-path"
                  className="input w-72"
                  value={loadPath}
                  onChange={(e) => setLoadPath(e.target.value)}
                  placeholder="path returned by save"
                />
              </div>
              <button
                type="button"
                className="btn btn-danger"
                disabled={!loadPath.trim()}
                onClick={() => setConfirming("load")}
              >
                Load…
              </button>
            </div>
            <button
              type="button"
              className="btn btn-danger ml-auto"
              data-testid="tabularasa"
              onClick={() => setConfirming("tabularasa")}
            >
              Tabula rasa…
            </button>
          </div>

          {lastMessage && (
            <div className="text-accent text-[12px]" data-testid="admin-message">
              {lastMessage}
            </div>
          )}
          {failed && <ErrorBox error={failed.error} />}
        </div>
      </section>

      <ConfirmDialog
        open={confirming === "tabularasa"}
        title="Erase all data"
        description="Tabula rasa removes every vertex, edge, and index. This cannot be undone."
        instanceName={instance.name}
        endpoint={describeEndpoint(instance)}
        confirmLabel="Erase everything"
        onConfirm={() => {
          setConfirming(null);
          erase.mutate();
        }}
        onCancel={() => setConfirming(null)}
      />
      <ConfirmDialog
        open={confirming === "load"}
        title="Load a checkpoint"
        description="Loading replaces the entire in-memory graph with the checkpoint."
        instanceName={instance.name}
        endpoint={describeEndpoint(instance)}
        confirmLabel="Load checkpoint"
        onConfirm={() => {
          setConfirming(null);
          load.mutate();
        }}
        onCancel={() => setConfirming(null)}
      />
    </div>
  );
}
