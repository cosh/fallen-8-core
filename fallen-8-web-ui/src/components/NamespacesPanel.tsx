// MIT License
//
// NamespacesPanel.tsx
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

import { useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useNavigate } from "@tanstack/react-router";
import { useRegistry, useActiveInstance, DEFAULT_NAMESPACE } from "../instances/registry";
import { describeEndpoint } from "../instances/types";
import {
  createNamespace,
  dropNamespace,
  listNamespaces,
  renameNamespace,
  setNamespaceLoadOnStartup,
} from "../api/endpoints";
import type { NamespaceEntry, NamespaceTriState } from "../api/types";
import { ApiError } from "../api/client";
import { migrateInstanceStore, purgeInstanceStore } from "../state/instanceStore";
import { DISPLAY_CAP, truncateChars } from "../lib/truncate";
import { SCROLL_ROWS, capList, scrollRows } from "../lib/listCaps";
import { isValidNamespaceName } from "../lib/namespaceName";
import { ABSENT, formatCountOrDash } from "../lib/format";
import { TAKES_EFFECT_ON_RESTART } from "../lib/restartCopy";
import { ConfirmDialog } from "./ConfirmDialog";
import { ErrorBox } from "./ErrorBox";
import { ListCapNote } from "./ListCapNote";
import { Truncated } from "./Truncated";

/**
 * Namespace management on the Connect screen (feature graph-namespaces): the full CRUD
 * table for the ACTIVE instance's namespaces - state, counts, URL prefix, the "at startup"
 * policy, rename / switch to / drop - plus the create form with live URL preview. "default"
 * aliases the bare (un-prefixed) routes and cannot be renamed, dropped, or excluded from a
 * boot. A drop is irreversible and demands the typed namespace name; save-game entries
 * remain valid restore points.
 */

/**
 * The "at startup" tri-state, in the server's own vocabulary (feature namespace-startup-load)
 * with the labels an operator reads. "inherit" clears the override rather than picking a side.
 */
const STARTUP_OPTIONS: { value: NamespaceTriState; label: string }[] = [
  { value: "enabled", label: "load" },
  { value: "disabled", label: "skip" },
  { value: "inherit", label: "inherit" },
];

/**
 * The "inherit" label, resolved against what the instance would actually do (feature
 * writable-instance-config 5.9). A bare "inherit" left an operator unable to tell whether it meant
 * load or skip, which is the whole question the row is asked.
 *
 * The two fields are published UNCOMPOSED on purpose, and both matter here: a startup mode of "all"
 * or "defaultOnly" SHORT-CIRCUITS every per-namespace preference, so under those modes the default is
 * not what inherit resolves to and saying otherwise would be a confident lie.
 */
export function inheritLabel(
  loadOnStartupDefault: boolean | undefined,
  startupLoadMode: string | undefined,
): string {
  if (startupLoadMode === "all") return "inherit (load: mode is all)";
  if (startupLoadMode === "defaultOnly") return "inherit (skip: mode is defaultOnly)";
  if (loadOnStartupDefault === undefined) return "inherit";
  return loadOnStartupDefault ? "inherit (load)" : "inherit (skip)";
}

/** What the message says the policy did. Every phrasing is about the NEXT start, never this one. */
const STARTUP_EFFECT: Record<NamespaceTriState, string> = {
  enabled: "will be loaded at the next start",
  disabled: "will be skipped at the next start",
  inherit: "follows the server default at the next start",
};

/**
 * The entry's override as the control's value. A non-boolean (null, or absent on an instance
 * predating the field) is "inherit": that is the truth for both, since neither carries an
 * override of its own.
 */
function startupValue(entry: NamespaceEntry): NamespaceTriState {
  // The reserved default is loaded whatever the catalog holds (the server refuses to exclude
  // it), so its disabled control states that rather than echoing an override nobody honours -
  // "inherit" there would read as "it depends on the server default", which it does not.
  if (entry.name === DEFAULT_NAMESPACE) return "enabled";
  if (typeof entry.loadOnStartupEnabled !== "boolean") return "inherit";
  return entry.loadOnStartupEnabled ? "enabled" : "disabled";
}


export function NamespacesPanel() {
  const instance = useActiveInstance();
  const setActiveNamespace = useRegistry((s) => s.setActiveNamespace);
  const navigate = useNavigate();
  const queryClient = useQueryClient();

  const [newName, setNewName] = useState("");
  const [renaming, setRenaming] = useState<string | null>(null);
  const [renameTo, setRenameTo] = useState("");
  const [dropping, setDropping] = useState<NamespaceEntry | null>(null);
  const [message, setMessage] = useState<string | null>(null);

  const list = useQuery({
    queryKey: [instance?.id, "namespaces"],
    queryFn: ({ signal }) => listNamespaces(instance!, signal),
    enabled: instance !== null,
    refetchInterval: 15_000,
    retry: 0,
  });

  const invalidate = () =>
    queryClient.invalidateQueries({ queryKey: [instance?.id, "namespaces"] });

  const create = useMutation({
    mutationFn: (name: string) => createNamespace(instance!, name),
    onSuccess: (entry) => {
      setMessage(entry ? `Created namespace “${entry.name}”.` : "Created.");
      setNewName("");
      invalidate();
    },
  });

  const rename = useMutation({
    mutationFn: ({ from, to }: { from: string; to: string }) =>
      renameNamespace(instance!, from, to),
    onSuccess: (entry, { from, to }) => {
      // Rename is a pure address change: the workspace (canvas, drafts) follows the new
      // name, and if the RENAMED namespace was the active one, the active pointer follows
      // too - otherwise the session strands on the dead name and lands in the recover state.
      migrateInstanceStore(instance!.id, from, to);
      const registry = useRegistry.getState();
      if (registry.activeNamespaces[instance!.id] === from) {
        registry.setActiveNamespace(instance!.id, to);
      }
      setMessage(entry ? `Renamed “${from}” to “${entry.name}”.` : "Renamed.");
      setRenaming(null);
      setRenameTo("");
      invalidate();
    },
  });

  const policy = useMutation({
    mutationFn: ({ name, value }: { name: string; value: NamespaceTriState }) =>
      setNamespaceLoadOnStartup(instance!, name, value),
    onSuccess: (entry, { name, value }) => {
      // No workspace or registry bookkeeping: this changes the NEXT boot's selection, not the
      // running process, so nothing in this session is invalidated except the entry itself.
      setMessage(`“${entry?.name ?? name}” ${STARTUP_EFFECT[value]} - ${TAKES_EFFECT_ON_RESTART}.`);
      invalidate();
    },
  });

  const drop = useMutation({
    mutationFn: (name: string) => dropNamespace(instance!, name),
    onSuccess: (_result, name) => {
      // The graph is gone; a lingering workspace would resurface phantom elements if a
      // namesake is ever created.
      purgeInstanceStore(instance!.id, name);
      setMessage(`Dropped namespace “${name}”.`);
      invalidate();
    },
  });

  if (!instance) return null;

  // On a pre-namespace server the inventory 404s: the panel states that instead of erroring.
  const preNamespaceServer =
    list.isError && list.error instanceof ApiError && list.error.status === 404;

  const entries = list.data?.namespaces ?? [];
  // Cap + scroll the inventory so a large namespace set never grows the panel without bound.
  const shownNamespaces = capList(entries);
  const failed = [create, rename, drop, policy].find((m) => m.isError);
  const newNameValid = isValidNamespaceName(newName);

  const switchTo = (name: string) => {
    setActiveNamespace(instance.id, name);
    void navigate({ to: "/q/$ns/dashboard", params: { ns: name } });
  };

  // What the drop confirmation says is at stake. A namespace the server did not load reports no
  // counts, so the sentence names the data rather than inventing zeros for it - "0 vertices"
  // would make an irreversible drop look free.
  const droppingScale =
    typeof dropping?.vertexCount === "number"
      ? `its ${formatCountOrDash(dropping.vertexCount)} vertices and ${formatCountOrDash(dropping.edgeCount)} edges`
      : "its data on disk (this process reports no counts for a namespace it did not load)";

  return (
    <section className="panel" data-testid="namespaces-panel">
      <div className="panel-title">
        Namespaces — {instance.name}
        {list.data && (
          <span className="text-fg-faint normal-case" data-testid="namespaces-quota">
            {list.data.namespaces.length} / {list.data.maxNamespaces.toLocaleString()} namespaces
            · isolated graphs, switching never leaks results
          </span>
        )}
      </div>

      {preNamespaceServer ? (
        <p className="text-fg-faint p-3 text-[12px]">
          This server predates namespaces — everything lives in the one (implicit) graph.
        </p>
      ) : list.isError ? (
        <div className="p-3">
          <ErrorBox error={list.error} onRetry={() => list.refetch()} />
        </div>
      ) : (
        <>
          {/* Scroll within the panel rather than spilling the actions column past its right
              edge when a row's content (long name + url prefix + 3 action buttons) is wide;
              `scroll-list` also caps the height so a large inventory never grows the page. */}
          <div className="scroll-list" style={scrollRows(SCROLL_ROWS.namespaces)}>
          <table className="w-full text-[12px]">
            <thead>
              <tr className="text-fg-faint">
                <th className="table-cell w-6"></th>
                <th className="table-cell">name</th>
                <th className="table-cell">vertices</th>
                <th className="table-cell">edges</th>
                <th className="table-cell">created</th>
                <th className="table-cell">url prefix</th>
                <th className="table-cell">at startup</th>
                <th className="table-cell w-56">actions</th>
              </tr>
            </thead>
            <tbody>
              {shownNamespaces.shown.map((entry) => (
                <tr key={entry.name} data-testid={`namespace-row-${entry.name}`}>
                  <td className="table-cell">
                    <span
                      title={entry.state}
                      className={entry.state === "ready" ? "text-accent" : "text-fg-faint"}
                    >
                      {entry.state === "ready" ? "●" : "◐"}
                    </span>
                  </td>
                  <td className="table-cell font-semibold">
                    <Truncated text={entry.name} max={DISPLAY_CAP.name} />
                    {entry.name === DEFAULT_NAMESPACE && (
                      <span className="text-fg-faint ml-2 font-normal">alias of bare URLs</span>
                    )}
                  </td>
                  {/* A namespace the server did not load reports no counts at all, so these
                      render a dash: a "0" would say a graph that still holds data is empty. */}
                  <td className="table-cell">{formatCountOrDash(entry.vertexCount)}</td>
                  <td className="table-cell">{formatCountOrDash(entry.edgeCount)}</td>
                  <td className="table-cell text-fg-dim">
                    {entry.createdAt ? new Date(entry.createdAt).toLocaleDateString() : ABSENT}
                  </td>
                  <td className="table-cell text-fg-dim whitespace-nowrap">
                    <Truncated text={`/ns/${entry.name}/*`} max={DISPLAY_CAP.path} middle />
                  </td>
                  {/* Whether the NEXT boot loads this namespace. The reserved default has no
                      choice to offer - every bare URL aliases it, so the server refuses the
                      field with 409 - and its reason is rendered as TEXT under the disabled
                      control, because a reason that only exists in a tooltip is a dead end on
                      touch. The row keeps a control either way so the column stays scannable.
                      Under rather than beside: as one nowrap line the reason made this column
                      wide enough to push the actions column out of the panel's viewport. */}
                  <td className="table-cell">
                    <select
                      className="input w-auto"
                      data-testid={`namespace-startup-${entry.name}`}
                      aria-label={`Load “${entry.name}” at startup`}
                      value={startupValue(entry)}
                      disabled={entry.name === DEFAULT_NAMESPACE || policy.isPending}
                      title={
                        entry.name === DEFAULT_NAMESPACE
                          ? "The reserved default namespace is always loaded: every bare URL aliases it"
                          : `Whether the next boot loads this namespace - ${TAKES_EFFECT_ON_RESTART}`
                      }
                      onChange={(e) =>
                        policy.mutate({
                          name: entry.name,
                          value: e.target.value as NamespaceTriState,
                        })
                      }
                    >
                      {STARTUP_OPTIONS.map((option) => (
                        <option key={option.value} value={option.value}>
                          {option.value === "inherit"
                            ? inheritLabel(
                                list.data?.loadOnStartupDefault,
                                list.data?.startupLoadMode,
                              )
                            : option.label}
                        </option>
                      ))}
                    </select>
                    {entry.name === DEFAULT_NAMESPACE && (
                      <div className="text-fg-faint">always loaded: bare URLs alias it</div>
                    )}
                  </td>
                  <td className="table-cell whitespace-nowrap">
                    {renaming === entry.name ? (
                      <form
                        className="flex gap-1"
                        onSubmit={(e) => {
                          e.preventDefault();
                          if (isValidNamespaceName(renameTo)) {
                            rename.mutate({ from: entry.name, to: renameTo });
                          }
                        }}
                      >
                        <input
                          className="input w-32"
                          data-testid={`rename-input-${entry.name}`}
                          value={renameTo}
                          onChange={(e) => setRenameTo(e.target.value)}
                          placeholder="new-name"
                          maxLength={63}
                          autoFocus
                        />
                        <button
                          type="submit"
                          className="btn"
                          disabled={!isValidNamespaceName(renameTo) || rename.isPending}
                        >
                          OK
                        </button>
                        <button type="button" className="btn" onClick={() => setRenaming(null)}>
                          ✕
                        </button>
                      </form>
                    ) : (
                      <div className="flex gap-1">
                        <button
                          type="button"
                          className="btn"
                          data-testid={`namespace-rename-${entry.name}`}
                          disabled={entry.name === DEFAULT_NAMESPACE}
                          title={
                            entry.name === DEFAULT_NAMESPACE
                              ? "The reserved default namespace cannot be renamed"
                              : "Rename (a pure address change — the data and its on-disk location stay put)"
                          }
                          onClick={() => {
                            setRenaming(entry.name);
                            setRenameTo(entry.name);
                          }}
                        >
                          Rename
                        </button>
                        <button
                          type="button"
                          className="btn"
                          data-testid={`namespace-switch-${entry.name}`}
                          onClick={() => switchTo(entry.name)}
                        >
                          Switch to
                        </button>
                        <button
                          type="button"
                          className="btn btn-danger"
                          data-testid={`namespace-drop-${entry.name}`}
                          disabled={entry.name === DEFAULT_NAMESPACE}
                          title={
                            entry.name === DEFAULT_NAMESPACE
                              ? "The reserved default namespace cannot be dropped"
                              : "Drop this namespace (irreversible)"
                          }
                          onClick={() => setDropping(entry)}
                        >
                          Drop
                        </button>
                      </div>
                    )}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
          </div>
          <ListCapNote shown={shownNamespaces.shown.length} total={shownNamespaces.total} />

          <div className="border-line space-y-2 border-t p-3">
            <form
              className="flex flex-col gap-1"
              onSubmit={(e) => {
                e.preventDefault();
                if (newNameValid) create.mutate(newName);
              }}
            >
              <label htmlFor="namespace-create-name" className="text-fg-faint text-[11px] uppercase">
                new namespace — becomes the URL segment (any name up to 63 chars; not “/” or “\”)
              </label>
              {/* Input flexes to fill the row; the button is pinned right at a fixed position,
                  and the URL preview lives on its own line so a long name never shifts it. */}
              <div className="flex items-center gap-2">
                <input
                  id="namespace-create-name"
                  className="input min-w-0 flex-1"
                  data-testid="namespace-create-name"
                  value={newName}
                  onChange={(e) => setNewName(e.target.value)}
                  placeholder="e.g. fraud-q3"
                  maxLength={63}
                />
                <button
                  type="submit"
                  className="btn btn-accent shrink-0"
                  data-testid="namespace-create"
                  disabled={!newNameValid || create.isPending}
                >
                  Create namespace
                </button>
              </div>
              <span
                className="text-fg-faint h-4 truncate text-[11px]"
                data-testid="namespace-url-preview"
                title={newName ? `PUT /ns/${newName}` : undefined}
              >
                {newName ? `PUT /ns/${truncateChars(newName, DISPLAY_CAP.path)}` : ""}
              </span>
            </form>
            <p className="text-fg-faint text-[11px]">
              409 = name exists · 404 on /ns/{"{name}"}/* = namespace missing (dropped elsewhere —
              screens then offer “recreate or switch”) · quota exceeded = 422 with the configured
              limit in the body
            </p>
            {/* The startup-only caveat this paragraph used to spell out now lives in one place,
                src/lib/restartCopy.ts, and the "inherit" option resolves itself in the control, so
                repeating either here would be a second home for the same fact. What survives is the
                part the control cannot say: what a namespace that was not loaded behaves like. */}
            <p className="text-fg-faint text-[11px]" data-testid="namespace-startup-note">
              at startup = whether the next boot loads this namespace; it {TAKES_EFFECT_ON_RESTART},
              so nothing is loaded or unloaded in the running process. A namespace that was not
              loaded reports no counts and answers 503 on every route but /status. The instance-wide
              default is a setting in the Configuration panel.
            </p>
            {message && (
              <div className="text-accent text-[12px]" data-testid="namespace-message">
                {message}
              </div>
            )}
            {failed && <ErrorBox error={failed.error} />}
          </div>
        </>
      )}

      <ConfirmDialog
        open={dropping !== null}
        title={`Drop namespace “${dropping?.name ?? ""}”`}
        description={`DELETE /ns/${dropping?.name ?? ""} - drops this namespace with ${droppingScale}. There is no undo; save-game entries that contain it remain valid restore points.`}
        instanceName={dropping?.name ?? ""}
        endpoint={describeEndpoint(instance)}
        confirmLabel="Drop namespace"
        onConfirm={() => {
          const name = dropping!.name;
          setDropping(null);
          drop.mutate(name);
        }}
        onCancel={() => setDropping(null)}
      />
    </section>
  );
}
