import { useInstanceStore } from "../instances/registry";
import { useStatus } from "../state/status";
import { ErrorBox } from "../components/ErrorBox";
import { Stat } from "../components/Stat";
import { Truncated } from "../components/Truncated";

/**
 * Dashboard (FR-2/3/4): the status overview for the active namespace — vertex/edge counts
 * and memory, from /status. It is deliberately lean: the sample gallery, the persistence/
 * administration actions, the stored-query library, the plugin registry, and the instance
 * configuration (semantic providers, observability) each have their own home (Samples, Save
 * games, Query, Plugins, and the Connect Configuration section).
 */
export function DashboardScreen() {
  const { instance } = useInstanceStore();
  const namespace = instance.namespace ?? "default";
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

      <p className="text-fg-faint text-[12px]">
        Semantic providers (embedding + chat), observability, and the security posture for
        this instance are on the{" "}
        <span className="text-fg-dim">Connect</span> screen's Configuration section.
      </p>
    </div>
  );
}
