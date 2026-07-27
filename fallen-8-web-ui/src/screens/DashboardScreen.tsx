import { useEffect } from "react";
import { useNavigate } from "@tanstack/react-router";
import { useInstanceStore } from "../instances/registry";
import { useStatus } from "../state/status";
import { ErrorBox } from "../components/ErrorBox";
import { Stat } from "../components/Stat";
import { Truncated } from "../components/Truncated";
import { FirstRunShow } from "../firstrun/FirstRunShow";
import { useFirstRun } from "../firstrun/firstRunStore";

/**
 * Dashboard (FR-2/3/4): the status overview for the active namespace — vertex/edge counts
 * and memory, from /status. It is deliberately lean: the sample gallery, the persistence/
 * administration actions, the stored-query library, the plugin registry, and the instance
 * configuration (semantic providers, observability) each have their own home (Samples, Save
 * games, Query, Plugins, and the Connect Configuration section).
 *
 * On an empty, not-yet-dismissed namespace this is also the home of the first-run show
 * (feature studio-first-run): instead of three zeroed tiles a newcomer gets an animated,
 * read-only walkthrough that creates nothing. It is dismissed per namespace and re-armed the
 * moment the namespace is seen non-empty, so a returning user is never nagged.
 */
export function DashboardScreen() {
  const { instance } = useInstanceStore();
  const namespace = instance.namespace ?? "default";
  const status = useStatus(instance);
  const navigate = useNavigate();

  const key = instance.id; // bound id (<instance>/<ns>): per-namespace dismissal
  const dismissed = useFirstRun((s) => s.dismissed[key] ?? false);
  const dismiss = useFirstRun((s) => s.dismiss);
  const clearIfPopulated = useFirstRun((s) => s.clearIfPopulated);

  // Re-arm the auto-show once the namespace is seen non-empty, so a graph that genuinely
  // empties later shows the intro again.
  const vertexCount = status.data?.vertexCount ?? null;
  useEffect(() => {
    if (vertexCount !== null && vertexCount > 0) clearIfPopulated(key);
  }, [vertexCount, key, clearIfPopulated]);

  if (status.isPending) {
    return <div className="text-fg-faint">Loading status…</div>;
  }
  if (status.isError) {
    return <ErrorBox error={status.error} onRetry={() => status.refetch()} />;
  }
  const data = status.data!;

  if (data.vertexCount === 0 && !dismissed) {
    return (
      <div className="h-full">
        <FirstRunShow
          variant="auto"
          onExplore={() => dismiss(key)}
          onImport={() => void navigate({ to: "/save-games" })}
          // Jump to the Sample gallery: the newcomer's path from empty to a curated, populated
          // graph. The show itself writes nothing; the unittest graph is test-only (see CLAUDE.md).
          onBrowseSamples={() =>
            void navigate({ to: "/q/$ns/samples", params: { ns: namespace } })
          }
        />
      </div>
    );
  }

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
