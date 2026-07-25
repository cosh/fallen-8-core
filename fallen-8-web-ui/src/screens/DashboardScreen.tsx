import { useInstanceStore } from "../instances/registry";
import { useEmbeddingProvider } from "../state/graphShape";
import { useStatus } from "../state/status";
import { ErrorBox } from "../components/ErrorBox";
import { Stat } from "../components/Stat";
import { Truncated } from "../components/Truncated";

/**
 * Dashboard (FR-2/3/4): the status overview for the active namespace — vertex/edge counts,
 * memory, and the embedding-provider card, all from /status. It is deliberately lean: the
 * sample gallery, the persistence/administration actions, the stored-query library, and the
 * plugin registry each have their own rail entry (Samples, Save games, Query, Plugins).
 */
export function DashboardScreen() {
  const { instance } = useInstanceStore();
  const namespace = instance.namespace ?? "default";
  const provider = useEmbeddingProvider(instance);
  const status = useStatus(instance);

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
        <h1 className="text-fg flex min-w-0 items-baseline gap-1 text-sm font-bold tracking-wider uppercase">
          <span className="shrink-0">Dashboard —</span>
          <Truncated text={instance.name} max={24} />
          <span className="shrink-0">/</span>
          <Truncated text={namespace} max={32} />
        </h1>
        <button type="button" className="btn ml-auto shrink-0" onClick={() => status.refetch()}>
          Refresh
        </button>
      </div>

      <div className="grid grid-cols-2 gap-3 md:grid-cols-3">
        <Stat label="vertices" value={data.vertexCount.toLocaleString()} />
        <Stat label="edges" value={data.edgeCount.toLocaleString()} />
        <Stat label="used memory" value={`${(data.usedMemory / 1024 / 1024).toFixed(1)} MiB`} />
      </div>

      <section className="panel" data-testid="embedding-provider-card">
        <div className="panel-title">
          Embedding provider
          <span className="text-fg-faint normal-case">feature embedding-provider</span>
        </div>
        {provider === null ? (
          <p className="text-fg-faint p-3 text-[12px]" data-testid="provider-unknown">
            This server has not reported its provider state yet (it may predate the
            /status embedding field). Pasting vectors and bound indices work regardless.
          </p>
        ) : !provider.enabled ? (
          <p className="text-fg-dim p-3 text-[12px]" data-testid="provider-disabled">
            Off on this instance — text-in embedding and semantic search answer 403;
            bring-your-own-vector paths work as normal. Enable it via the docker
            environment (F8_EMBEDDINGS, on by default) or the Fallen8:Embedding config
            section (see features/done/embedding-provider).
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
    </div>
  );
}
