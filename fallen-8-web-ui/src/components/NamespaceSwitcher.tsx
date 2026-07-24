import { useEffect, useRef, useState } from "react";
import { useMutation, useQueryClient } from "@tanstack/react-query";
import { useNavigate } from "@tanstack/react-router";
import type { InstanceConfig } from "../instances/types";
import type { NamespaceEntry } from "../api/types";
import { createNamespace } from "../api/endpoints";
import { ApiError } from "../api/client";
import { DEFAULT_NAMESPACE } from "../instances/registry";
import { isValidNamespaceName } from "../lib/namespaceName";
import { Truncated } from "./Truncated";

/**
 * The top-bar namespace switcher (feature graph-namespaces, per the approved mock): a
 * trigger showing the active namespace with its counts, and a dropdown with a filter,
 * per-namespace rows (state dot, counts, active / bare-URL-alias / not-ready tags), an
 * inline "+ New namespace" create that switches to the newborn, a "Manage…" jump to the
 * Connect panel, and the quota footer. Full CRUD stays on the Connect screen.
 */


function formatCount(value: number): string {
  return value.toLocaleString();
}

export function NamespaceSwitcher({
  instance,
  entries,
  maxNamespaces,
  activeNamespace,
  onSwitch,
}: {
  instance: InstanceConfig;
  entries: NamespaceEntry[];
  maxNamespaces: number | null;
  activeNamespace: string;
  onSwitch: (name: string) => void;
}) {
  const [open, setOpen] = useState(false);
  const [filter, setFilter] = useState("");
  const [creating, setCreating] = useState(false);
  const [newName, setNewName] = useState("");
  const containerRef = useRef<HTMLDivElement>(null);
  const navigate = useNavigate();
  const queryClient = useQueryClient();

  // Outside click / Escape close the dropdown (and reset its transient state).
  useEffect(() => {
    if (!open) return;
    const onMouseDown = (event: MouseEvent) => {
      if (!containerRef.current?.contains(event.target as Node)) close();
    };
    const onKeyDown = (event: KeyboardEvent) => {
      if (event.key === "Escape") close();
    };
    document.addEventListener("mousedown", onMouseDown);
    document.addEventListener("keydown", onKeyDown);
    return () => {
      document.removeEventListener("mousedown", onMouseDown);
      document.removeEventListener("keydown", onKeyDown);
    };
  }, [open]);

  const close = () => {
    setOpen(false);
    setFilter("");
    setCreating(false);
    setNewName("");
    create.reset();
  };

  const create = useMutation({
    mutationFn: (name: string) => createNamespace(instance, name),
    onSuccess: (entry) => {
      void queryClient.invalidateQueries({ queryKey: [instance.id, "namespaces"] });
      close();
      if (entry) onSwitch(entry.name);
    },
  });

  const active = entries.find((entry) => entry.name === activeNamespace);
  const visible = entries.filter((entry) =>
    entry.name.toLowerCase().includes(filter.trim().toLowerCase()),
  );
  const newNameValid = isValidNamespaceName(newName);
  const createError =
    create.error instanceof ApiError
      ? create.error.status === 409
        ? "name exists (409)"
        : create.error.status === 422
          ? "quota exceeded (422)"
          : `failed (${create.error.status})`
      : create.error
        ? "failed"
        : null;

  return (
    <div ref={containerRef} className="relative">
      <button
        type="button"
        data-testid="namespace-switcher"
        aria-haspopup="listbox"
        aria-expanded={open}
        className="input flex w-auto max-w-[20rem] min-w-44 cursor-pointer items-center gap-2 text-left"
        onClick={() => (open ? close() : setOpen(true))}
      >
        <span aria-hidden className="text-accent shrink-0">●</span>
        <Truncated text={activeNamespace} className="min-w-0 font-semibold" />
        {active && (
          <span className="text-fg-faint shrink-0 text-[11px]">
            {formatCount(active.vertexCount)} v · {formatCount(active.edgeCount)} e
          </span>
        )}
        <span aria-hidden className="text-fg-faint ml-auto shrink-0">▾</span>
      </button>

      {open && (
        <div
          data-testid="namespace-dropdown"
          role="listbox"
          className="panel border-line absolute top-full left-0 z-50 mt-1 w-96 border shadow-lg"
        >
          <div className="border-line border-b p-2">
            <input
              data-testid="namespace-filter"
              className="input w-full"
              placeholder="filter namespaces…"
              value={filter}
              onChange={(e) => setFilter(e.target.value)}
              autoFocus
            />
          </div>

          <div className="max-h-72 overflow-auto py-1">
            {visible.map((entry) => (
              <button
                key={entry.name}
                type="button"
                role="option"
                aria-selected={entry.name === activeNamespace}
                data-testid={`namespace-option-${entry.name}`}
                className="hover:bg-panel-2 flex w-full items-center gap-2 px-3 py-1.5 text-left text-[12px]"
                onClick={() => {
                  close();
                  onSwitch(entry.name);
                }}
              >
                <span
                  aria-hidden
                  className={`shrink-0 ${entry.state === "ready" ? "text-accent" : "text-fg-faint"}`}
                >
                  {entry.state === "ready" ? "●" : "◐"}
                </span>
                <Truncated text={entry.name} className="text-fg min-w-0 font-semibold" />
                <span className="text-fg-faint shrink-0">
                  {formatCount(entry.vertexCount)} v · {formatCount(entry.edgeCount)} e
                </span>
                <span className="text-fg-faint ml-auto shrink-0 text-[10px] tracking-wider uppercase">
                  {entry.name === activeNamespace
                    ? "active"
                    : entry.state !== "ready"
                      ? "not ready"
                      : entry.name === DEFAULT_NAMESPACE
                        ? "bare-URL alias"
                        : ""}
                </span>
              </button>
            ))}
            {visible.length === 0 && (
              <div className="text-fg-faint px-3 py-2 text-[12px]">no namespace matches</div>
            )}
          </div>

          <div className="border-line space-y-2 border-t p-2">
            {creating ? (
              <form
                className="flex items-center gap-2"
                onSubmit={(e) => {
                  e.preventDefault();
                  if (newNameValid && !create.isPending) create.mutate(newName);
                }}
              >
                <input
                  data-testid="namespace-quick-create-name"
                  className="input w-full"
                  placeholder="name — becomes the URL segment"
                  value={newName}
                  onChange={(e) => setNewName(e.target.value)}
                  maxLength={63}
                  autoFocus
                />
                <button
                  type="submit"
                  data-testid="namespace-quick-create"
                  className="btn btn-accent whitespace-nowrap"
                  disabled={!newNameValid || create.isPending}
                >
                  Create
                </button>
              </form>
            ) : (
              <div className="flex gap-2">
                <button
                  type="button"
                  data-testid="namespace-new"
                  className="btn btn-accent flex-1"
                  onClick={() => setCreating(true)}
                >
                  + New namespace
                </button>
                <button
                  type="button"
                  data-testid="namespace-manage"
                  className="btn flex-1"
                  onClick={() => {
                    close();
                    void navigate({ to: "/" });
                  }}
                >
                  Manage…
                </button>
              </div>
            )}
            {createError && (
              <div className="text-danger text-[11px]" data-testid="namespace-quick-create-error">
                {createError}
              </div>
            )}
            <p className="text-fg-faint text-[10px]" data-testid="namespace-dropdown-footer">
              {entries.length} / {maxNamespaces?.toLocaleString() ?? "—"} namespaces · switching
              remounts the active screen — results never leak across namespaces
            </p>
          </div>
        </div>
      )}
    </div>
  );
}
