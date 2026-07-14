import type { ReactNode } from "react";
import { Link, useRouterState } from "@tanstack/react-router";
import { useQuery } from "@tanstack/react-query";
import { useRegistry, useActiveInstance } from "../instances/registry";
import { describeEndpoint } from "../instances/types";
import { getStatus } from "../api/endpoints";

const NAV = [
  { to: "/", label: "Connect", icon: "◉" },
  { to: "/dashboard", label: "Dashboard", icon: "▦" },
  { to: "/browser", label: "Browser", icon: "☰" },
  { to: "/query", label: "Query", icon: "∴" },
  { to: "/path", label: "Path", icon: "↝" },
  { to: "/subgraphs", label: "Subgraph", icon: "◫" },
  { to: "/canvas", label: "Canvas", icon: "❉" },
] as const;

/** Health chip for the active instance - reflects the disconnected state (scenario 9). */
function HealthChip() {
  const instance = useActiveInstance();
  const health = useQuery({
    queryKey: [instance?.id, "status"],
    queryFn: ({ signal }) => getStatus(instance!, signal),
    enabled: instance !== null,
    refetchInterval: 15_000,
    retry: 0,
  });

  if (!instance) return null;
  const state = health.isError ? "down" : health.isSuccess ? "up" : "…";
  return (
    <span
      data-testid="health-chip"
      className={`rounded border px-1.5 py-0.5 text-[10px] tracking-wider uppercase ${
        state === "up"
          ? "border-accent/40 text-accent"
          : state === "down"
            ? "border-danger/50 text-danger"
            : "border-line text-fg-faint"
      }`}
    >
      {state === "up" ? "online" : state === "down" ? "unreachable" : "checking"}
    </span>
  );
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

  return (
    <div className="flex h-full">
      <nav className="border-line bg-panel flex w-16 shrink-0 flex-col items-center gap-1 border-r py-3">
        <div className="text-accent mb-3 text-lg font-bold" title="F8 Studio">
          F8
        </div>
        {NAV.map((item) => (
          <Link
            key={item.to}
            to={item.to}
            title={item.label}
            className={`flex w-14 flex-col items-center gap-0.5 rounded px-1 py-2 text-center transition-colors ${
              pathname === item.to
                ? "bg-panel-2 text-accent"
                : "text-fg-dim hover:text-fg"
            }`}
          >
            <span aria-hidden className="text-base leading-none">
              {item.icon}
            </span>
            <span className="text-[9px] tracking-wide uppercase">{item.label}</span>
          </Link>
        ))}
      </nav>

      <div className="flex min-w-0 flex-1 flex-col">
        <header className="border-line bg-panel flex h-11 shrink-0 items-center gap-3 border-b px-3">
          <label htmlFor="instance-switcher" className="text-fg-faint text-[11px] uppercase">
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
          <div className="ml-auto">
            <HealthChip />
          </div>
        </header>

        <main className="min-h-0 flex-1 overflow-auto p-4">
          {active ? (
            children
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
