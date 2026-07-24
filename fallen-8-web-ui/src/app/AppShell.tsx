import { useEffect, type ReactNode } from "react";
import { Link, useNavigate, useRouterState } from "@tanstack/react-router";
import { useQuery } from "@tanstack/react-query";
import {
  useRegistry,
  useActiveInstance,
  useActiveNamespace,
  DEFAULT_NAMESPACE,
} from "../instances/registry";
import { describeEndpoint, type InstanceConfig } from "../instances/types";
import { ApiError } from "../api/client";
import { getStatus, isAuthorized, listNamespaces } from "../api/endpoints";
import { useLiveChangeFeed, type LiveFeedStatus } from "../state/liveFeed";
import { NamespaceSwitcher } from "../components/NamespaceSwitcher";
import { help } from "../lib/fieldHelp";

/**
 * Navigation: Connect, Save games and Benchmark are Fallen-8-level (flat routes); the rest
 * operate on the ACTIVE NAMESPACE and live under /q/{ns}/… (feature graph-namespaces).
 */
const NAV = [
  { leaf: "/", label: "Connect", icon: "◉", scoped: false },
  { leaf: "dashboard", label: "Dashboard", icon: "▦", scoped: true },
  { leaf: "/save-games", label: "Save games", icon: "⭯", scoped: false },
  { leaf: "browser", label: "Browser", icon: "☰", scoped: true },
  { leaf: "query", label: "Query", icon: "∴", scoped: true },
  { leaf: "indexes", label: "Indexes", icon: "⌗", scoped: true },
  { leaf: "path", label: "Path", icon: "↝", scoped: true },
  { leaf: "subgraphs", label: "Subgraph", icon: "◫", scoped: true },
  { leaf: "analytics", label: "Analytics", icon: "∑", scoped: true },
  { leaf: "canvas", label: "Canvas", icon: "❉", scoped: true },
  { leaf: "/benchmarks", label: "Benchmark", icon: "◔", scoped: false },
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
 * App shell: left icon rail + top bar with the instance / namespace pair (FR-1b +
 * feature graph-namespaces). Every screen renders under a bar that names the active
 * instance, the active namespace, and the resulting endpoint prefix, so production is
 * never mistaken for local and namespaces are never implicit.
 */
export function AppShell({ children }: { children: ReactNode }) {
  const instances = useRegistry((s) => s.instances);
  const activeId = useRegistry((s) => s.activeId);
  const setActive = useRegistry((s) => s.setActive);
  const setActiveNamespace = useRegistry((s) => s.setActiveNamespace);
  const active = useActiveInstance();
  const ns = useActiveNamespace();
  const navigate = useNavigate();
  const pathname = useRouterState({ select: (s) => s.location.pathname });
  // One change feed stream per ACTIVE instance + namespace, torn down on either switch
  // (FR-1c + feature graph-namespaces): the feed streams /ns/{ns}/changefeed and its
  // invalidations hit the per-namespace query keys (the bound view's compound id).
  const feedInstance = active ? { ...active, id: `${active.id}/${ns}`, namespace: ns } : null;
  const liveStatus = useLiveChangeFeed(feedInstance);
  const connection = useConnectionState(active);

  // The namespace inventory feeds the switcher (name + counts + quota) AND the capability
  // probe: a 404 marks the server pre-namespace, and every screen then degrades to the
  // unbound (bare-path) view instead of 404ing on /ns/default (see useInstanceStore).
  const setNamespaceSupport = useRegistry((s) => s.setNamespaceSupport);
  const namespaceSupported = useRegistry((s) =>
    active ? s.namespaceSupport[active.id] : undefined,
  );
  const namespaces = useQuery({
    queryKey: [active?.id, "namespaces"],
    queryFn: ({ signal }) => listNamespaces(active!, signal),
    enabled: active !== null && connection === "connected",
    refetchInterval: 15_000,
    retry: 0,
  });
  useEffect(() => {
    if (!active) return;
    if (namespaces.data) {
      setNamespaceSupport(active.id, true);
    } else if (namespaces.error instanceof ApiError && namespaces.error.status === 404) {
      setNamespaceSupport(active.id, false);
    }
  }, [active?.id, namespaces.data, namespaces.error, setNamespaceSupport]);
  const namespaceEntries = namespaces.data?.namespaces ?? [
    { name: DEFAULT_NAMESPACE, state: "ready" as const, vertexCount: 0, edgeCount: 0, createdAt: "" },
  ];

  const leafOf = (path: string) => (path.startsWith("/q/") ? path.split("/").slice(3).join("/") : "");

  const switchNamespace = (name: string) => {
    if (active) setActiveNamespace(active.id, name);
    // Stay on the current scoped screen when there is one; land on the dashboard otherwise.
    void navigate({
      to: `/q/$ns/${leafOf(pathname) || "dashboard"}` as "/q/$ns/dashboard",
      params: { ns: name },
    });
  };

  const switchInstance = (id: string) => {
    setActive(id);
    // Under a namespaced URL, restore the NEW instance's remembered namespace - never stamp
    // the previous instance's namespace onto it via the URL-sync effect.
    if (pathname.startsWith("/q/")) {
      const remembered = useRegistry.getState().activeNamespaces[id] || DEFAULT_NAMESPACE;
      void navigate({
        to: `/q/$ns/${leafOf(pathname) || "dashboard"}` as "/q/$ns/dashboard",
        params: { ns: remembered },
      });
    }
  };

  /** The concrete path a nav item points at (scoped items resolve against the active ns). */
  const navTarget = (item: (typeof NAV)[number]): string =>
    item.scoped ? `/q/${ns}/${item.leaf}` : item.leaf;

  /** The route mask Link navigates by (params supply the namespace). */
  const navMask = (item: (typeof NAV)[number]): string =>
    item.scoped ? `/q/$ns/${item.leaf}` : item.leaf;

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
          const target = navTarget(item);
          const inner = (
            <>
              <span aria-hidden className="text-base leading-none">
                {item.icon}
              </span>
              <span className="text-[9px] tracking-wide uppercase">{item.label}</span>
            </>
          );
          // Everything beyond Connect is locked until the active instance is connected.
          if (item.leaf !== "/" && connection !== "connected") {
            return (
              <span
                key={item.label}
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
              key={item.label}
              to={navMask(item) as "/q/$ns/dashboard"}
              params={{ ns }}
              data-testid={testid}
              title={item.label}
              className={`flex w-14 flex-col items-center gap-0.5 rounded px-1 py-2 text-center transition-colors ${
                pathname === target
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
        {/* Two equal halves: the instance group fills the LEFT half, the namespace group +
            endpoint left-align from the midpoint in the RIGHT half, and the status chips stay
            pinned to the far-right corner. */}
        <header className="border-line bg-panel grid h-11 shrink-0 grid-cols-2 items-center gap-3 border-b px-3">
          <div className="flex min-w-0 items-center gap-3">
            <label
              htmlFor="instance-switcher"
              className="text-fg-faint label-help shrink-0 text-[11px] uppercase"
              title={help("instanceSwitcher")}
            >
              instance
            </label>
            {/* flex-1 makes the select fill the rest of the left half (.input is w-full, which
                flex-1's basis overrides for the flex main size). */}
            <select
              id="instance-switcher"
              data-testid="instance-switcher"
              className="input min-w-0 flex-1"
              value={activeId ?? ""}
              onChange={(e) => switchInstance(e.target.value)}
            >
              {instances.map((instance) => (
                <option key={instance.id} value={instance.id}>
                  {instance.name}
                </option>
              ))}
            </select>
          </div>

          <div className="flex min-w-0 items-center gap-3">
            {active && namespaceSupported === false && (
              <span data-testid="active-endpoint" className="text-fg-dim min-w-0 truncate text-[12px]">
                {describeEndpoint(active)} (pre-namespace server)
              </span>
            )}
            {active && namespaceSupported !== false && (
              <>
                <span
                  className="text-fg-faint shrink-0 text-[11px] uppercase"
                  title="The active namespace: an isolated graph inside this Fallen-8. Manage namespaces on the Connect screen."
                >
                  namespace
                </span>
                <NamespaceSwitcher
                  instance={active}
                  entries={namespaceEntries}
                  maxNamespaces={namespaces.data?.maxNamespaces ?? null}
                  activeNamespace={ns}
                  onSwitch={switchNamespace}
                />
                <span data-testid="active-endpoint" className="text-fg-dim min-w-0 truncate text-[12px]">
                  {describeEndpoint(active)} → /ns/{ns}/*
                </span>
              </>
            )}
            <div className="ml-auto flex shrink-0 items-center gap-2">
              <LiveChip status={liveStatus} />
              <HealthChip state={connection} />
            </div>
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
              // Key the screen subtree by instance id + namespace so switching EITHER
              // remounts the active screen (FR-1c + feature graph-namespaces):
              // component-local result sets (browser lookups, query hydrations, path
              // results) are dropped rather than carried into another context. Per-
              // namespace stores are already isolated; this closes the leak one layer
              // above them.
              <div key={`${active.id}/${ns}`} className="h-full">
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
