import type { ReactNode } from "react";
import { Link, useRouterState } from "@tanstack/react-router";
import { useQuery } from "@tanstack/react-query";
import { useRegistry, useActiveInstance } from "../instances/registry";
import { describeEndpoint, type InstanceConfig } from "../instances/types";
import { getStatus, isAuthorized } from "../api/endpoints";
import { useLiveChangeFeed, type LiveFeedStatus } from "../state/liveFeed";
import { help } from "../lib/fieldHelp";

const NAV = [
  { to: "/", label: "Connect", icon: "◉" },
  { to: "/dashboard", label: "Dashboard", icon: "▦" },
  { to: "/save-games", label: "Save games", icon: "⭯" },
  { to: "/browser", label: "Browser", icon: "☰" },
  { to: "/query", label: "Query", icon: "∴" },
  { to: "/indexes", label: "Indexes", icon: "⌗" },
  { to: "/path", label: "Path", icon: "↝" },
  { to: "/subgraphs", label: "Subgraph", icon: "◫" },
  { to: "/analytics", label: "Analytics", icon: "∑" },
  { to: "/canvas", label: "Canvas", icon: "❉" },
] as const;

/**
 * Connection state of the ACTIVE instance, shared by the health chip and the nav gate.
 * "connected" means the /status probe answered AND the credential is authorized
 * (isAuthorized) - so a missing or wrong API key reads as "unauthorized", never as
 * online, and the nav beyond Connect stays locked in every state but "connected".
 */
type ConnectionState = "connected" | "unauthorized" | "unreachable" | "checking" | "none";

function useConnectionState(instance: InstanceConfig | null): ConnectionState {
  const health = useQuery({
    queryKey: [instance?.id, "status"],
    queryFn: ({ signal }) => getStatus(instance!, signal),
    enabled: instance !== null,
    refetchInterval: 15_000,
    retry: 0,
  });

  if (!instance) return "none";
  if (health.isError) return "unreachable";
  if (!health.isSuccess) return "checking";
  // A body-less success has no auth verdict - same treatment as a pre-auth server.
  return health.data === null || isAuthorized(health.data) ? "connected" : "unauthorized";
}

/** Health chip for the active instance - reflects the disconnected state (scenario 9). */
function HealthChip({ state }: { state: ConnectionState }) {
  if (state === "none") return null;
  return (
    <span
      data-testid="health-chip"
      className={`rounded border px-1.5 py-0.5 text-[10px] tracking-wider uppercase ${
        state === "connected"
          ? "border-accent/40 text-accent"
          : state === "checking"
            ? "border-line text-fg-faint"
            : "border-danger/50 text-danger"
      }`}
    >
      {state === "connected" ? "online" : state === "checking" ? "checking" : state}
    </span>
  );
}

/**
 * Live-mode chip (feature change-feed): "live" while the active instance's change feed
 * stream is up, a quiet "live off" when the server answered 503 (feed disabled) - the
 * screens then simply stay in their polling behaviour. Nothing while connecting.
 */
function LiveChip({ status }: { status: LiveFeedStatus }) {
  if (status === "live") {
    return (
      <span
        data-testid="live-chip"
        title="Live updates: streaming committed changes from this instance"
        className="border-accent/40 text-accent rounded border px-1.5 py-0.5 text-[10px] tracking-wider uppercase"
      >
        live
      </span>
    );
  }
  if (status === "unavailable") {
    return (
      <span
        data-testid="live-chip"
        title="Live updates unavailable (change feed disabled on this instance) - falling back to polling"
        className="border-line text-fg-faint rounded border px-1.5 py-0.5 text-[10px] tracking-wider uppercase"
      >
        live off
      </span>
    );
  }
  return null;
}

/**
 * App shell: left icon rail + top bar with the always-visible instance switcher (FR-1b).
 * Every screen renders under a bar that names the active instance and its endpoint, so
 * production is never mistaken for local.
 */
export function AppShell({ children }: { children: ReactNode }) {
  const instances = useRegistry((s) => s.instances);
  const activeId = useRegistry((s) => s.activeId);
  const setActive = useRegistry((s) => s.setActive);
  const active = useActiveInstance();
  const pathname = useRouterState({ select: (s) => s.location.pathname });
  // One change feed stream per ACTIVE instance, torn down on switch (FR-1c).
  const liveStatus = useLiveChangeFeed(active);
  const connection = useConnectionState(active);

  return (
    <div className="flex h-full">
      <nav className="border-line bg-panel flex w-16 shrink-0 flex-col items-center gap-1 border-r py-3">
        <img
          src="/F8White.svg"
          alt="F8 Studio"
          title="F8 Studio"
          className="mb-3 w-12"
        />
        {NAV.map((item) => {
          const testid = `nav-${item.label.toLowerCase().replace(/\s+/g, "-")}`;
          const inner = (
            <>
              <span aria-hidden className="text-base leading-none">
                {item.icon}
              </span>
              <span className="text-[9px] tracking-wide uppercase">{item.label}</span>
            </>
          );
          // Everything beyond Connect is locked until the active instance is connected.
          if (item.to !== "/" && connection !== "connected") {
            return (
              <span
                key={item.to}
                data-testid={testid}
                aria-disabled="true"
                title={`${item.label} — needs a connected instance (see Connect)`}
                className="text-fg-faint flex w-14 cursor-not-allowed flex-col items-center gap-0.5 rounded px-1 py-2 text-center opacity-50"
              >
                {inner}
              </span>
            );
          }
          return (
            <Link
              key={item.to}
              to={item.to}
              data-testid={testid}
              title={item.label}
              className={`flex w-14 flex-col items-center gap-0.5 rounded px-1 py-2 text-center transition-colors ${
                pathname === item.to
                  ? "bg-panel-2 text-accent"
                  : "text-fg-dim hover:text-fg"
              }`}
            >
              {inner}
            </Link>
          );
        })}
      </nav>

      <div className="flex min-w-0 flex-1 flex-col">
        <header className="border-line bg-panel flex h-11 shrink-0 items-center gap-3 border-b px-3">
          <label
            htmlFor="instance-switcher"
            className="text-fg-faint label-help text-[11px] uppercase"
            title={help("instanceSwitcher")}
          >
            instance
          </label>
          <select
            id="instance-switcher"
            data-testid="instance-switcher"
            className="input w-auto min-w-40"
            value={activeId ?? ""}
            onChange={(e) => setActive(e.target.value)}
          >
            {instances.map((instance) => (
              <option key={instance.id} value={instance.id}>
                {instance.name}
              </option>
            ))}
          </select>
          {active && (
            <span data-testid="active-endpoint" className="text-fg-dim truncate text-[12px]">
              {describeEndpoint(active)}
            </span>
          )}
          <div className="ml-auto flex items-center gap-2">
            <LiveChip status={liveStatus} />
            <HealthChip state={connection} />
          </div>
        </header>

        <main className="min-h-0 flex-1 overflow-auto p-4">
          {active ? (
            connection === "unauthorized" && pathname !== "/" ? (
              // Deep links cannot bypass the nav gate when the instance definitively
              // rejected the credential. Transient states (checking/unreachable) keep the
              // screen mounted - a 15s health blip must not throw away in-progress work.
              <div data-testid="connection-guard" className="text-fg-dim">
                “{active.name}” rejected the credential — set its API key on the{" "}
                <Link to="/" className="text-accent underline">
                  Connect
                </Link>{" "}
                screen.
              </div>
            ) : (
              // Key the screen subtree by instance id so switching instances REMOUNTS the
              // active screen (FR-1c): component-local result sets (browser lookups, query
              // hydrations, path results) are dropped rather than carried into another
              // instance's context. Per-instance stores are already isolated; this closes
              // the leak one layer above them.
              <div key={active.id} className="h-full">
                {children}
              </div>
            )
          ) : (
            <div className="text-fg-dim">
              No instance selected — register one on the Connect screen.
            </div>
          )}
        </main>
      </div>
    </div>
  );
}
