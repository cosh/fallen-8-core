// MIT License
//
// NamespaceSwitcher.tsx
//
// Copyright (c) 2011-2026 Henning Rauch
//
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in all
// copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
//
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
// SOFTWARE.

import { useEffect, useRef, useState } from "react";
import { useMutation, useQueryClient } from "@tanstack/react-query";
import { useNavigate } from "@tanstack/react-router";
import type { InstanceConfig } from "../instances/types";
import type { NamespaceEntry } from "../api/types";
import { createNamespace } from "../api/endpoints";
import { ApiError } from "../api/client";
import { DEFAULT_NAMESPACE } from "../instances/registry";
import { isValidNamespaceName } from "../lib/namespaceName";
import { formatCountOrDash } from "../lib/format";
import { Truncated } from "./Truncated";

/**
 * The top-bar namespace switcher (feature graph-namespaces, per the approved mock): a
 * trigger showing the active namespace with its counts, and a dropdown with a filter,
 * per-namespace rows (state dot, counts, active / bare-URL-alias / not-ready / not-loaded
 * tags), an
 * inline "+ New namespace" create that switches to the newborn, a "Manage…" jump to the
 * Connect panel, and the quota footer. Full CRUD stays on the Connect screen.
 *
 * This component renders in the app shell on EVERY screen, so it must survive any entry the
 * inventory can contain: a namespace the server did not load carries null counts, and throwing
 * on one of them would take the whole Studio down with the error boundary - including the
 * Namespaces panel an operator would use to undo the exclusion. Hence `formatCountOrDash`.
 */

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
    <div ref={containerRef} className="relative min-w-44 flex-auto">
      {/* The trigger FILLS the room its container gives it (flex-1 + w-full) instead of hugging its
          text: in the top bar it is the one growing element between the fixed namespace label and
          the right-pinned status chips, so whatever the bar reserves for the namespace side is
          occupied by the name and the counts rather than left as dead space. It also means the
          counts never run out of room at their worst ("12,345,678 v · 98,765,432 e"), and under
          real pressure the name truncates before them (Truncated is the only min-w-0 child). */}
      <button
        type="button"
        data-testid="namespace-switcher"
        aria-haspopup="listbox"
        aria-expanded={open}
        className="input flex w-full cursor-pointer items-center gap-2 text-left"
        onClick={() => (open ? close() : setOpen(true))}
      >
        {/* The trigger's dot is the residency signal on EVERY screen: an active namespace the
            server did not load takes the same faint "◐" the dropdown rows already use for a
            non-ready one, so nothing new enters the visual vocabulary. An entry the inventory
            does not carry (offline, still loading) keeps today's accent dot. */}
        <span
          aria-hidden
          title={active?.state}
          className={`shrink-0 ${active && active.state !== "ready" ? "text-fg-faint" : "text-accent"}`}
        >
          {active && active.state !== "ready" ? "◐" : "●"}
        </span>
        <Truncated text={activeNamespace} className="min-w-0 font-semibold" />
        {active && (
          <span className="text-fg-faint shrink-0 text-[11px]">
            {formatCountOrDash(active.vertexCount)} v · {formatCountOrDash(active.edgeCount)} e
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
                  {formatCountOrDash(entry.vertexCount)} v · {formatCountOrDash(entry.edgeCount)} e
                </span>
                {/* Residency outranks "active" and "bare-URL alias" in the one tag slot: both of
                    those are already said elsewhere (the trigger names the active namespace and
                    aria-selected marks it; the Namespaces panel names the alias), while a
                    not-loaded namespace unannounced is the one state that changes what every
                    screen can do - it answers 503. "not ready" stays the word for "creating". */}
                <span className="text-fg-faint ml-auto shrink-0 text-[10px] tracking-wider uppercase">
                  {entry.state === "notLoaded"
                    ? "not loaded"
                    : entry.name === activeNamespace
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
