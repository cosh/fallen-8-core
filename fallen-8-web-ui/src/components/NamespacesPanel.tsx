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
} from "../api/endpoints";
import type { NamespaceEntry } from "../api/types";
import { ApiError } from "../api/client";
import { migrateInstanceStore, purgeInstanceStore } from "../state/instanceStore";
import { DISPLAY_CAP, truncateChars } from "../lib/truncate";
import { ConfirmDialog } from "./ConfirmDialog";
import { ErrorBox } from "./ErrorBox";
import { Truncated } from "./Truncated";

/**
 * Namespace management on the Connect screen (feature graph-namespaces): the full CRUD
 * table for the ACTIVE instance's namespaces — state, counts, URL prefix, rename / switch
 * to / drop — plus the create form with live URL preview. "default" aliases the bare
 * (un-prefixed) routes and cannot be renamed or dropped. A drop is irreversible and
 * demands the typed namespace name; save-game entries remain valid restore points.
 */

const NAME_PATTERN = /^[a-z0-9-]{1,63}$/;

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
  const failed = [create, rename, drop].find((m) => m.isError);
  const newNameValid = NAME_PATTERN.test(newName);

  const switchTo = (name: string) => {
    setActiveNamespace(instance.id, name);
    void navigate({ to: "/q/$ns/dashboard", params: { ns: name } });
  };

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
              edge when a row's content (long name + url prefix + 3 action buttons) is wide. */}
          <div className="overflow-x-auto">
          <table className="w-full text-[12px]">
            <thead>
              <tr className="text-fg-faint">
                <th className="table-cell w-6"></th>
                <th className="table-cell">name</th>
                <th className="table-cell">vertices</th>
                <th className="table-cell">edges</th>
                <th className="table-cell">created</th>
                <th className="table-cell">url prefix</th>
                <th className="table-cell w-56">actions</th>
              </tr>
            </thead>
            <tbody>
              {entries.map((entry) => (
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
                  <td className="table-cell">{entry.vertexCount.toLocaleString()}</td>
                  <td className="table-cell">{entry.edgeCount.toLocaleString()}</td>
                  <td className="table-cell text-fg-dim">
                    {entry.createdAt ? new Date(entry.createdAt).toLocaleDateString() : "—"}
                  </td>
                  <td className="table-cell text-fg-dim whitespace-nowrap">
                    <Truncated text={`/ns/${entry.name}/*`} max={DISPLAY_CAP.path} middle />
                  </td>
                  <td className="table-cell whitespace-nowrap">
                    {renaming === entry.name ? (
                      <form
                        className="flex gap-1"
                        onSubmit={(e) => {
                          e.preventDefault();
                          if (NAME_PATTERN.test(renameTo)) {
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
                          disabled={!NAME_PATTERN.test(renameTo) || rename.isPending}
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

          <div className="border-line space-y-2 border-t p-3">
            <form
              className="flex flex-col gap-1"
              onSubmit={(e) => {
                e.preventDefault();
                if (newNameValid) create.mutate(newName);
              }}
            >
              <label htmlFor="namespace-create-name" className="text-fg-faint text-[11px] uppercase">
                new namespace — [a-z0-9-]{"{1,63}"}, becomes the URL segment
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
        description={`DELETE /ns/${dropping?.name ?? ""} — drops this namespace with its ${dropping?.vertexCount.toLocaleString() ?? 0} vertices and ${dropping?.edgeCount.toLocaleString() ?? 0} edges. There is no undo; save-game entries that contain it remain valid restore points.`}
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
