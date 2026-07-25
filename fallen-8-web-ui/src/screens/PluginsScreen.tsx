import { useInstanceStore } from "../instances/registry";
import { useStatus } from "../state/status";
import { ErrorBox } from "../components/ErrorBox";
import { Truncated } from "../components/Truncated";
import { DISPLAY_CAP } from "../lib/truncate";
import { PluginsPanel } from "../components/PluginsPanel";

/**
 * Plugins (feature plugin-registration): the one home for everything plugin-related, its
 * own rail entry. The built-in families discovered on the engine (index / path / analytics)
 * come from GET /status; the registry table below (PluginsPanel) lists the namespace's
 * runtime-authored, compile-validated plugins and owns the register/inspect/run/delete flow.
 * Namespace-scoped — registrations live per graph. Service plugins are intentionally not
 * shown: none ship built-in and there is no service-authoring surface.
 */
function PluginList({ title, plugins }: { title: string; plugins: string[] }) {
  return (
    <div className="panel">
      <div className="panel-title">{title}</div>
      <ul className="p-3 text-[12px]">
        {plugins.length === 0 && <li className="text-fg-faint">none</li>}
        {plugins.map((plugin) => (
          <li key={plugin} className="text-fg-dim">
            <Truncated text={plugin} max={DISPLAY_CAP.name} />
          </li>
        ))}
      </ul>
    </div>
  );
}

export function PluginsScreen() {
  const { instance } = useInstanceStore();
  const namespace = instance.namespace ?? "default";
  const status = useStatus(instance);

  return (
    <div className="mx-auto max-w-5xl space-y-4">
      <div className="flex items-center gap-2">
        <h1 className="text-fg flex min-w-0 items-baseline gap-1 text-sm font-bold tracking-wider uppercase">
          <span className="shrink-0">Plugins —</span>
          <Truncated text={instance.name} max={24} />
          <span className="shrink-0">/</span>
          <Truncated text={namespace} max={32} />
        </h1>
        <button
          type="button"
          className="btn ml-auto shrink-0"
          onClick={() => status.refetch()}
        >
          Refresh
        </button>
      </div>

      {status.isError ? (
        <ErrorBox error={status.error} onRetry={() => status.refetch()} />
      ) : (
        <div className="grid grid-cols-1 gap-3 md:grid-cols-3">
          <PluginList title="Index plugins" plugins={status.data?.availableIndexPlugins ?? []} />
          <PluginList title="Path plugins" plugins={status.data?.availablePathPlugins ?? []} />
          <PluginList
            title="Analytics plugins"
            plugins={status.data?.availableAnalyticsPlugins ?? []}
          />
        </div>
      )}

      <PluginsPanel />
    </div>
  );
}
