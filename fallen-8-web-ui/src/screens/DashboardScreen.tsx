import { useRef, useState } from "react";
import { useMutation, useQueryClient } from "@tanstack/react-query";
import { useActiveInstance } from "../instances/registry";
import { describeEndpoint } from "../instances/types";
import {
  exportBulk,
  generateSampleGraph,
  importBulk,
  loadGraph,
  runBenchmark,
  saveGraph,
  tabulaRasa,
  trimGraph,
} from "../api/endpoints";
import { ApiError } from "../api/client";
import { embeddingProvider, shapeSuggestions, useGraphShape } from "../state/graphShape";
import { useStatus } from "../state/status";
import { ErrorBox } from "../components/ErrorBox";
import { Field } from "../components/Field";
import { ConfirmDialog } from "../components/ConfirmDialog";
import { Stat } from "../components/Stat";
import { StoredQueriesPanel } from "../components/StoredQueriesPanel";
import { formatCompact, formatExact } from "../lib/format";

/**
 * Dashboard (FR-2/3/4): counts, memory, plugin lists from /status; admin actions with
 * typed confirmation for the destructive ones (load, tabula rasa) naming the instance
 * (FR-1d); demo-data playground (generate/benchmark). A 500 from save/load is a rolled
 * back transaction and renders as a real failure.
 */

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
  const [showExportFilter, setShowExportFilter] = useState(false);
  const [exportVertexLabel, setExportVertexLabel] = useState("");
  const [exportEdgeLabel, setExportEdgeLabel] = useState("");
  const importFileRef = useRef<HTMLInputElement>(null);
  const shape = useGraphShape(instance).data;
  const suggestions = shapeSuggestions(shape);
  const provider = embeddingProvider(shape);

  const status = useStatus(instance);

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

  const exportGraph = useMutation({
    mutationFn: () =>
      exportBulk(instance, {
        vertexLabel: exportVertexLabel.trim() || undefined,
        edgeLabel: exportEdgeLabel.trim() || undefined,
      }),
    onSuccess: (blob) => {
      const url = URL.createObjectURL(blob);
      const anchor = document.createElement("a");
      anchor.href = url;
      anchor.download = `${instance.name}.jsonl`;
      anchor.click();
      URL.revokeObjectURL(url);
      setLastMessage(`Exported ${(blob.size / 1024 / 1024).toFixed(1)} MiB of jsonl.`);
    },
  });

  const importGraph = useMutation({
    mutationFn: async (file: File) => {
      if (file.size > 64 * 1024 * 1024) {
        setLastMessage(
          "Large file — the browser buffers the whole upload with no resumability; curl is the better tool from here up.",
        );
      }
      return await importBulk(instance, file);
    },
    onSuccess: (result) => {
      setLastMessage(
        result
          ? `Imported ${result.verticesCreated.toLocaleString()} vertices and ${result.edgesCreated.toLocaleString()} edges (${result.linesRead.toLocaleString()} lines read).`
          : "Imported.",
      );
      refresh();
    },
  });

  const failed = [save, load, trim, erase, generate, exportGraph].find(
    (m) => m.isError,
  );

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

      <div className="grid grid-cols-2 gap-3 md:grid-cols-3">
        <Stat label="vertices" value={data.vertexCount.toLocaleString()} />
        <Stat label="edges" value={data.edgeCount.toLocaleString()} />
        <Stat label="used memory" value={`${(data.usedMemory / 1024 / 1024).toFixed(1)} MiB`} />
      </div>

      <div className="grid grid-cols-1 gap-3 md:grid-cols-2 xl:grid-cols-4">
        <PluginList title="Index plugins" plugins={data.availableIndexPlugins ?? []} />
        <PluginList title="Path plugins" plugins={data.availablePathPlugins ?? []} />
        <PluginList
          title="Analytics plugins"
          plugins={data.availableAnalyticsPlugins ?? []}
        />
        <PluginList title="Service plugins" plugins={data.availableServicePlugins ?? []} />
      </div>

      <section className="panel" data-testid="embedding-provider-card">
        <div className="panel-title">
          Embedding provider
          <span className="text-fg-faint normal-case">feature element-embeddings</span>
        </div>
        {provider === null ? (
          <p className="text-fg-faint p-3 text-[12px]" data-testid="provider-unknown">
            Provider status is part of the Graph shape snapshot — Compute it on the
            Analytics screen to see the active backend and model. (Pasting vectors and
            bound indices work regardless.)
          </p>
        ) : !provider.enabled ? (
          <p className="text-fg-dim p-3 text-[12px]" data-testid="provider-disabled">
            Off on this instance (Fallen8:Embedding:Enabled). Text-in embedding and semantic
            search are disabled; bring-your-own-vector paths work as normal.
          </p>
        ) : (
          <div
            className="grid grid-cols-2 gap-3 p-3 md:grid-cols-3"
            data-testid="provider-enabled"
          >
            <Stat label="backend" value={provider.backend ?? "—"} />
            <Stat
              label="model"
              value={
                provider.modelName
                  ? provider.modelName + (provider.modelVersion ? `@${provider.modelVersion}` : "")
                  : "—"
              }
            />
            <Stat label="dimension" value={String(provider.dimension)} />
            <Stat label="metric" value={provider.intendedMetric ?? "—"} />
            <Stat label="loaded" value={provider.loaded ? "yes" : "not yet"} />
          </div>
        )}
      </section>

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
              <Field helpKey="loadPath" label="load path" htmlFor="load-path">
                <input
                  id="load-path"
                  className="input w-72"
                  value={loadPath}
                  onChange={(e) => setLoadPath(e.target.value)}
                  placeholder="path returned by save"
                />
              </Field>
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

          <div className="border-line space-y-2 border-t pt-3">
            <div className="text-fg-faint text-[10px] tracking-widest uppercase">
              interchange (jsonl)
            </div>
            <div className="flex flex-wrap items-end gap-2">
              <button
                type="button"
                className="btn"
                data-testid="bulk-export"
                disabled={exportGraph.isPending}
                title="internally consistent interchange — not a crash-consistent backup; use save games for point-in-time"
                onClick={() => exportGraph.mutate()}
              >
                {exportGraph.isPending ? "Exporting…" : "Export .jsonl"}
              </button>
              <button
                type="button"
                className="btn"
                onClick={() => setShowExportFilter((s) => !s)}
              >
                {showExportFilter ? "Hide" : "Filter by label"}
              </button>
              {showExportFilter && (
                <>
                  <Field
                    helpKey="exportVertexLabel"
                    label="vertex label"
                    htmlFor="export-vertex-label"
                  >
                    <input
                      id="export-vertex-label"
                      className="input w-36"
                      list="dash-vertex-labels"
                      value={exportVertexLabel}
                      onChange={(e) => setExportVertexLabel(e.target.value)}
                    />
                  </Field>
                  <Field
                    helpKey="exportEdgeLabel"
                    label="edge label"
                    htmlFor="export-edge-label"
                  >
                    <input
                      id="export-edge-label"
                      className="input w-36"
                      list="dash-edge-labels"
                      value={exportEdgeLabel}
                      onChange={(e) => setExportEdgeLabel(e.target.value)}
                    />
                  </Field>
                </>
              )}
              <button
                type="button"
                className="btn ml-4"
                data-testid="bulk-import"
                disabled={importGraph.isPending}
                title="imports into an EMPTY graph only — the server enforces this with a 409"
                onClick={() => importFileRef.current?.click()}
              >
                {importGraph.isPending ? "Importing…" : "Import .jsonl…"}
              </button>
              <input
                ref={importFileRef}
                type="file"
                accept=".jsonl,.ndjson,application/x-ndjson"
                className="hidden"
                onChange={(e) => {
                  const file = e.target.files?.[0];
                  e.target.value = "";
                  if (file) importGraph.mutate(file);
                }}
              />
            </div>
            {importGraph.isError && (
              <div className="space-y-1" data-testid="import-error">
                <ErrorBox error={importGraph.error} />
                {importGraph.error instanceof ApiError &&
                  importGraph.error.status === 409 && (
                    <p className="text-fg-dim text-[12px]">
                      Target graph is not empty — Tabula rasa first, or import into a
                      fresh instance.
                    </p>
                  )}
              </div>
            )}
          </div>

          {lastMessage && (
            <div className="text-accent text-[12px]" data-testid="admin-message">
              {lastMessage}
            </div>
          )}
          {failed && <ErrorBox error={failed.error} />}
        </div>
      </section>

      <StoredQueriesPanel />

      <datalist id="dash-vertex-labels">
        {suggestions.vertexLabels.map((label) => (
          <option key={label} value={label} />
        ))}
      </datalist>
      <datalist id="dash-edge-labels">
        {suggestions.edgeLabels.map((label) => (
          <option key={label} value={label} />
        ))}
      </datalist>

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
