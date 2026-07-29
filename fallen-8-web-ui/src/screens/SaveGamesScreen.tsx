// MIT License
//
// SaveGamesScreen.tsx
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

import { useRef, useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useActiveInstance, useInstanceStore } from "../instances/registry";
import { describeEndpoint } from "../instances/types";
import {
  deleteSaveGame,
  exportBulk,
  importBulk,
  listSaveGames,
  loadGraph,
  loadSaveGame,
  saveAllNamespaces,
  saveGraph,
  tabulaRasa,
  tabulaRasaAll,
  trimGraph,
} from "../api/endpoints";
import type { SaveGame, SaveGameNamespace } from "../api/types";
import { ApiError } from "../api/client";
import { invalidateInstanceQueries } from "../api/queries";
import { getInstanceStore, purgeAllInstanceStores } from "../state/instanceStore";
import { shapeSuggestions, useGraphShape } from "../state/graphShape";
import { formatExact } from "../lib/format";
import { ErrorBox } from "../components/ErrorBox";
import { ConfirmDialog } from "../components/ConfirmDialog";
import { ListCapNote } from "../components/ListCapNote";
import { Field } from "../components/Field";
import { help } from "../lib/fieldHelp";
import { SCROLL_ROWS, capList, scrollRows } from "../lib/listCaps";

/**
 * Save games (feature save-games + graph-namespaces): the persistence home. The top is the
 * **Administration** section — the persistence/lifecycle and jsonl interchange actions that used
 * to live on the Dashboard: those are NAMESPACE-scoped (they act on the active namespace shown in
 * the top bar), so they run through the namespace-bound instance. Below it is the Fallen-8-level
 * checkpoint registry (using the raw Fallen-8-level instance) — an entry can span several
 * namespaces ("Save all" creates one), and loading restores exactly the namespaces an entry
 * contains (or one of them). The registry lists every entry (up to the LIST_MAX_ROWS ceiling) and
 * caps its height / scrolls once it grows past SCROLL_ROWS.saveGames rows, so a long save history
 * never grows the page. Sits under Dashboard in the rail.
 */

function formatBytes(bytes: number): string {
  if (bytes <= 0) return "0 B";
  const units = ["B", "KiB", "MiB", "GiB"];
  const exp = Math.min(Math.floor(Math.log(bytes) / Math.log(1024)), units.length - 1);
  return `${(bytes / 1024 ** exp).toFixed(exp === 0 ? 0 : 1)} ${units[exp]}`;
}

function formatSavedAt(iso: string): string {
  const t = Date.parse(iso);
  return Number.isNaN(t) ? iso : new Date(t).toLocaleString();
}

/**
 * The namespaces an entry effectively contains — mirrors the server's normalization: a
 * pre-namespace (v1) entry is a default-only save described by its top-level fields.
 */
export function effectiveNamespaces(game: SaveGame): SaveGameNamespace[] {
  if (game.namespaces && game.namespaces.length > 0) return game.namespaces;
  return [
    {
      name: "default",
      location: game.location ?? "",
      fileCount: game.fileCount,
      totalBytes: game.totalBytes,
      kpis: game.kpis ?? {
        vertexCount: 0,
        edgeCount: 0,
        usedMemoryBytes: 0,
        indices: [],
        availableIndexPlugins: [],
        availablePathPlugins: [],
        availableServicePlugins: [],
        subGraphs: [],
      },
    },
  ];
}

export function SaveGamesScreen() {
  // The registry is Fallen-8-level: raw instance, raw query keys, raw workspace stores.
  const instance = useActiveInstance()!;
  // The Administration actions act on the ACTIVE namespace (top bar): namespace-bound instance.
  const { instance: ns } = useInstanceStore();
  const namespace = ns.namespace ?? "default";
  const queryClient = useQueryClient();
  const [message, setMessage] = useState<string | null>(null);
  const [confirming, setConfirming] = useState<
    { kind: "load" | "delete"; game: SaveGame } | null
  >(null);
  const [deleteFiles, setDeleteFiles] = useState(false);
  /** "" = restore the entire entry; otherwise the one namespace to restore. */
  const [loadNamespace, setLoadNamespace] = useState("");

  // ---- Administration state (moved from the Dashboard) ----
  const [adminConfirming, setAdminConfirming] = useState<
    "tabularasa" | "factory-reset" | "load-path" | null
  >(null);
  const [loadPath, setLoadPath] = useState("");
  const [adminMessage, setAdminMessage] = useState<string | null>(null);
  const [showExportFilter, setShowExportFilter] = useState(false);
  const [exportVertexLabel, setExportVertexLabel] = useState("");
  const [exportEdgeLabel, setExportEdgeLabel] = useState("");
  const [exportEdgeType, setExportEdgeType] = useState("");
  const importFileRef = useRef<HTMLInputElement>(null);
  const suggestions = shapeSuggestions(useGraphShape(ns).data);

  const list = useQuery({
    queryKey: [instance.id, "savegames"],
    queryFn: ({ signal }) => listSaveGames(instance, signal),
  });

  // Raw + compound keys both: a restore changes the per-namespace caches (compound-keyed),
  // not just this Fallen-8-level screen's raw-keyed list.
  const invalidate = () => invalidateInstanceQueries(queryClient, instance.id);

  const saveAll = useMutation({
    mutationFn: () => saveAllNamespaces(instance),
    onSuccess: (entry) => {
      const members = entry ? effectiveNamespaces(entry) : [];
      setMessage(
        entry
          ? `Saved ${members.length} namespace${members.length === 1 ? "" : "s"}: ${members
              .map((m) => m.name)
              .join(", ")}`
          : "Saved.",
      );
      invalidate();
    },
  });

  const load = useMutation({
    mutationFn: ({ id, namespaceName }: { id: string; namespaceName?: string }) =>
      loadSaveGame(instance, id, namespaceName),
    onSuccess: (entry, variables) => {
      // A restore reassigns element ids, so the restored namespaces' persisted canvases
      // would silently render DIFFERENT elements under the old ids - clear them.
      if (entry) {
        const restored = variables.namespaceName
          ? effectiveNamespaces(entry).filter((m) => m.name === variables.namespaceName)
          : effectiveNamespaces(entry);
        for (const member of restored) {
          getInstanceStore(instance.id, member.name).getState().clearCanvas();
        }
      }
      setMessage(
        entry
          ? variables.namespaceName
            ? `Restored namespace “${variables.namespaceName}” from ${entry.id}.`
            : `Restored save game ${entry.id}.`
          : "Loaded.",
      );
      invalidate();
    },
  });

  const remove = useMutation({
    mutationFn: ({ id, files }: { id: string; files: boolean }) =>
      deleteSaveGame(instance, id, files),
    onSuccess: () => {
      setMessage("Save game deleted.");
      invalidate();
    },
  });

  // ---- Administration mutations (namespace-scoped, moved from the Dashboard) ----
  const save = useMutation({
    mutationFn: () => saveGraph(ns),
    onSuccess: (entry) => {
      setAdminMessage(
        entry
          ? `Saved namespace “${namespace}” to ${entry.location} — registered as save game ${entry.id} (it appears in the table above).`
          : "Saved.",
      );
      invalidate();
    },
  });
  const loadCheckpoint = useMutation({
    mutationFn: () => loadGraph(ns, loadPath),
    onSuccess: () => {
      setAdminMessage(`Loaded namespace “${namespace}” from ${loadPath}.`);
      invalidate();
    },
  });
  const trim = useMutation({
    mutationFn: () => trimGraph(ns),
    onSuccess: () => setAdminMessage("Trim requested."),
  });
  const erase = useMutation({
    mutationFn: () => tabulaRasa(ns),
    onSuccess: () => {
      setAdminMessage(`Namespace “${namespace}” erased.`);
      invalidate();
    },
  });
  const factoryReset = useMutation({
    mutationFn: () => tabulaRasaAll(ns),
    onSuccess: () => {
      setAdminMessage("Factory reset: every namespace dropped, “default” erased.");
      // Fallen-8-wide: every namespace's caches AND persisted workspaces (canvases would
      // resurface phantom elements) are stale now, not just this one's.
      purgeAllInstanceStores(instance.id.split("/")[0]);
      queryClient.invalidateQueries();
    },
  });
  const exportGraph = useMutation({
    mutationFn: () =>
      exportBulk(ns, {
        vertexLabel: exportVertexLabel.trim() || undefined,
        edgeLabel: exportEdgeLabel.trim() || undefined,
        edgePropertyId: exportEdgeType.trim() || undefined,
      }),
    onSuccess: (blob) => {
      const url = URL.createObjectURL(blob);
      const anchor = document.createElement("a");
      anchor.href = url;
      anchor.download = `${instance.name}-${namespace}.jsonl`;
      anchor.click();
      URL.revokeObjectURL(url);
      setAdminMessage(`Exported ${(blob.size / 1024 / 1024).toFixed(1)} MiB of jsonl.`);
    },
  });
  const importGraph = useMutation({
    mutationFn: async (file: File) => {
      if (file.size > 64 * 1024 * 1024) {
        setAdminMessage(
          "Large file — the browser buffers the whole upload with no resumability; curl is the better tool from here up.",
        );
      }
      return await importBulk(ns, file);
    },
    onSuccess: (result) => {
      setAdminMessage(
        result
          ? `Imported ${result.verticesCreated.toLocaleString()} vertices and ${result.edgesCreated.toLocaleString()} edges (${result.linesRead.toLocaleString()} lines read).`
          : "Imported.",
      );
      invalidate();
    },
  });

  const failed = [saveAll, load, remove].find((m) => m.isError);
  const adminFailed = [save, loadCheckpoint, trim, erase, factoryReset, exportGraph].find(
    (m) => m.isError,
  );

  const confirmingMembers = confirming ? effectiveNamespaces(confirming.game) : [];
  // Cap + scroll the registry so a long save history never grows the page unbounded.
  const games = capList(list.data ?? []);

  return (
    <div className="mx-auto max-w-6xl space-y-4">
      <div className="flex items-center gap-2">
        <h1 className="text-fg text-sm font-bold tracking-wider uppercase">
          Save games — {instance.name}
        </h1>
        <button
          type="button"
          className="btn btn-accent ml-auto"
          data-testid="save-now"
          title="Fallen-8-wide: checkpoints EVERY namespace into one restore point"
          disabled={saveAll.isPending}
          onClick={() => saveAll.mutate()}
        >
          {saveAll.isPending ? "Saving…" : "Save all namespaces"}
        </button>
        <button type="button" className="btn" onClick={() => list.refetch()}>
          Refresh
        </button>
      </div>

      {message && (
        <div className="text-accent text-[12px]" data-testid="savegame-message">
          {message}
        </div>
      )}
      {failed && <ErrorBox error={failed.error} />}

      {/* Administration (moved from the Dashboard): namespace-scoped persistence/lifecycle
          plus jsonl interchange. The destructive actions require typing the target name. */}
      <section className="panel" data-testid="administration">
        <div className="panel-title">
          Administration
          <span className="text-fg-faint normal-case">
            acts on namespace “{namespace}”
          </span>
        </div>
        <div className="space-y-3 p-3">
          <div className="flex flex-wrap items-end gap-2">
            <button
              type="button"
              className="btn"
              data-testid="save-namespace"
              title={`Checkpoints namespace “${namespace}” into a save-game entry`}
              disabled={save.isPending}
              onClick={() => save.mutate()}
            >
              {save.isPending ? "Saving…" : "Save namespace"}
            </button>
            <button
              type="button"
              className="btn"
              disabled={trim.isPending}
              onClick={() => trim.mutate()}
            >
              Trim
            </button>
            <div className="ml-4 flex items-end gap-2">
              <Field helpKey="loadPath" label="load path" htmlFor="load-path">
                <input
                  id="load-path"
                  className="input w-72"
                  value={loadPath}
                  onChange={(e) => setLoadPath(e.target.value)}
                  placeholder="path returned by save"
                />
              </Field>
              <button
                type="button"
                className="btn btn-danger"
                disabled={!loadPath.trim()}
                onClick={() => setAdminConfirming("load-path")}
              >
                Load…
              </button>
            </div>
            <button
              type="button"
              className="btn btn-danger ml-auto"
              data-testid="tabularasa"
              title={`Erases namespace “${namespace}” (it stays registered, empty)`}
              onClick={() => setAdminConfirming("tabularasa")}
            >
              Erase namespace…
            </button>
            <button
              type="button"
              className="btn btn-danger"
              data-testid="factory-reset"
              title="Fallen-8-wide: drops every non-default namespace and erases “default”"
              onClick={() => setAdminConfirming("factory-reset")}
            >
              Factory reset…
            </button>
          </div>

          <div className="border-line space-y-2 border-t pt-3">
            <div className="text-fg-faint text-[10px] tracking-widest uppercase">
              interchange (jsonl)
            </div>
            <div className="flex flex-wrap items-end gap-2">
              <button
                type="button"
                className="btn"
                data-testid="bulk-export"
                disabled={exportGraph.isPending}
                title="internally consistent interchange — not a crash-consistent backup; use save games for point-in-time"
                onClick={() => exportGraph.mutate()}
              >
                {exportGraph.isPending ? "Exporting…" : "Export .jsonl"}
              </button>
              <button
                type="button"
                className="btn"
                onClick={() => setShowExportFilter((s) => !s)}
              >
                {showExportFilter ? "Hide" : "Filter by label"}
              </button>
              {showExportFilter && (
                <>
                  <Field
                    helpKey="exportVertexLabel"
                    label="vertex label"
                    htmlFor="export-vertex-label"
                  >
                    <input
                      id="export-vertex-label"
                      className="input w-36"
                      list="savegame-vertex-labels"
                      value={exportVertexLabel}
                      onChange={(e) => setExportVertexLabel(e.target.value)}
                    />
                  </Field>
                  <Field
                    helpKey="exportEdgeLabel"
                    label="edge label"
                    htmlFor="export-edge-label"
                  >
                    <input
                      id="export-edge-label"
                      className="input w-36"
                      list="savegame-edge-labels"
                      value={exportEdgeLabel}
                      onChange={(e) => setExportEdgeLabel(e.target.value)}
                    />
                  </Field>
                  <Field
                    helpKey="exportEdgeType"
                    label="edge type"
                    htmlFor="export-edge-type"
                  >
                    <input
                      id="export-edge-type"
                      className="input w-36"
                      value={exportEdgeType}
                      onChange={(e) => setExportEdgeType(e.target.value)}
                    />
                  </Field>
                </>
              )}
              <button
                type="button"
                className="btn ml-4"
                data-testid="bulk-import"
                disabled={importGraph.isPending}
                title="imports into an EMPTY graph only — the server enforces this with a 409"
                onClick={() => importFileRef.current?.click()}
              >
                {importGraph.isPending ? "Importing…" : "Import .jsonl…"}
              </button>
              <input
                ref={importFileRef}
                type="file"
                accept=".jsonl,.ndjson,application/x-ndjson"
                className="hidden"
                onChange={(e) => {
                  const file = e.target.files?.[0];
                  e.target.value = "";
                  if (file) importGraph.mutate(file);
                }}
              />
            </div>
            {importGraph.isError && (
              <div className="space-y-1" data-testid="import-error">
                <ErrorBox error={importGraph.error} />
                {importGraph.error instanceof ApiError &&
                  importGraph.error.status === 409 && (
                    <p className="text-fg-dim text-[12px]">
                      Target graph is not empty — Erase namespace first, or import into a
                      fresh namespace.
                    </p>
                  )}
              </div>
            )}
          </div>

          {adminMessage && (
            <div className="text-accent wrap-break-word text-[12px]" data-testid="admin-message">
              {adminMessage}
            </div>
          )}
          {adminFailed && <ErrorBox error={adminFailed.error} />}
        </div>
      </section>

      <section className="panel">
        <div className="panel-title">
          registry
          <span className="text-fg-faint normal-case">
            metadata/savegames.json · Fallen-8-level · values captured at save time
          </span>
        </div>
        {list.isError && (
          <div className="p-3">
            <ErrorBox error={list.error} onRetry={() => list.refetch()} />
          </div>
        )}
        <div className="scroll-list" style={scrollRows(SCROLL_ROWS.saveGames)}>
          <table className="w-full text-[12px]">
            <thead>
              <tr className="text-fg-faint">
                <th className="table-cell">saved at</th>
                <th className="table-cell">trigger</th>
                <th className="table-cell">namespaces</th>
                <th className="table-cell">vertices</th>
                <th className="table-cell">edges</th>
                <th className="table-cell">files</th>
                <th className="table-cell">size</th>
                <th className="table-cell w-40">actions</th>
              </tr>
            </thead>
            <tbody>
              {games.shown.map((game) => {
                const members = effectiveNamespaces(game);
                return (
                  <tr key={game.id} data-testid={`savegame-row-${game.id}`} className="hover:bg-panel-2">
                    <td className="table-cell">{formatSavedAt(game.savedAt)}</td>
                    <td className="table-cell">
                      <span className="border-line rounded border px-1.5 py-0.5 text-[10px] tracking-wider uppercase">
                        {game.trigger}
                      </span>
                    </td>
                    <td
                      className="table-cell"
                      data-testid={`savegame-namespaces-${game.id}`}
                      title={members
                        .map((m) => `${m.name}: ${m.kpis.vertexCount} v · ${m.kpis.edgeCount} e`)
                        .join("\n")}
                    >
                      {members.map((m) => m.name).join(", ")}
                    </td>
                    <td className="table-cell">
                      {formatExact(members.reduce((sum, m) => sum + m.kpis.vertexCount, 0))}
                    </td>
                    <td className="table-cell">
                      {formatExact(members.reduce((sum, m) => sum + m.kpis.edgeCount, 0))}
                    </td>
                    <td className="table-cell">{game.fileCount}</td>
                    <td className="table-cell">{formatBytes(game.totalBytes)}</td>
                    <td className="table-cell">
                      <div className="flex gap-1">
                        <button
                          type="button"
                          className="btn"
                          data-testid={`load-${game.id}`}
                          onClick={() => {
                            setLoadNamespace("");
                            setConfirming({ kind: "load", game });
                          }}
                        >
                          Load…
                        </button>
                        <button
                          type="button"
                          className="btn btn-danger"
                          data-testid={`delete-${game.id}`}
                          onClick={() => {
                            setDeleteFiles(false);
                            setConfirming({ kind: "delete", game });
                          }}
                        >
                          Delete…
                        </button>
                      </div>
                    </td>
                  </tr>
                );
              })}
              {games.total === 0 && !list.isError && (
                <tr>
                  <td className="table-cell text-fg-faint" colSpan={8}>
                    No save games yet. “Save all namespaces” creates the first one; loading a
                    checkpoint on another instance registers it automatically.
                  </td>
                </tr>
              )}
            </tbody>
          </table>
        </div>
        <ListCapNote shown={games.shown.length} total={games.total} />
      </section>

      <datalist id="savegame-vertex-labels">
        {suggestions.vertexLabels.map((label) => (
          <option key={label} value={label} />
        ))}
      </datalist>
      <datalist id="savegame-edge-labels">
        {suggestions.edgeLabels.map((label) => (
          <option key={label} value={label} />
        ))}
      </datalist>

      <ConfirmDialog
        open={confirming?.kind === "load"}
        title="Restore save game"
        description={
          loadNamespace
            ? `Restores ONLY namespace “${loadNamespace}” to this entry's content (recreating it if dropped). Every other namespace stays untouched.`
            : `Restores the namespaces this entry contains — ${confirmingMembers
                .map((m) => m.name)
                .join(", ")} — replacing their current content (dropped ones are recreated). Namespaces the entry does not contain stay untouched.`
        }
        instanceName={instance.name}
        endpoint={describeEndpoint(instance)}
        confirmLabel="Restore"
        extra={
          confirmingMembers.length > 1 ? (
            <label className="text-fg-dim flex items-center gap-2 text-[12px]" data-testid="load-namespace-select">
              restore
              <select
                className="input w-auto"
                value={loadNamespace}
                onChange={(e) => setLoadNamespace(e.target.value)}
              >
                <option value="">entire entry ({confirmingMembers.length} namespaces)</option>
                {confirmingMembers.map((m) => (
                  <option key={m.name} value={m.name}>
                    only “{m.name}”
                  </option>
                ))}
              </select>
            </label>
          ) : undefined
        }
        onConfirm={() => {
          const game = confirming!.game;
          const namespaceName = loadNamespace || undefined;
          setConfirming(null);
          load.mutate({ id: game.id, namespaceName });
        }}
        onCancel={() => setConfirming(null)}
      />

      <ConfirmDialog
        open={confirming?.kind === "delete"}
        title="Delete save game"
        description="Removes this save game from the registry."
        instanceName={instance.name}
        endpoint={describeEndpoint(instance)}
        confirmLabel="Delete save game"
        extra={
          <label
            className="text-fg-dim label-help flex items-center gap-2 text-[12px]"
            title={help("saveGameDeleteFiles")}
            data-testid="delete-files-toggle"
          >
            <input
              type="checkbox"
              checked={deleteFiles}
              onChange={(e) => setDeleteFiles(e.target.checked)}
            />
            also delete the checkpoint files on disk
          </label>
        }
        onConfirm={() => {
          const game = confirming!.game;
          const files = deleteFiles;
          setConfirming(null);
          remove.mutate({ id: game.id, files });
        }}
        onCancel={() => setConfirming(null)}
      />

      <ConfirmDialog
        open={adminConfirming === "tabularasa"}
        title={`Erase namespace “${namespace}”`}
        description={`Removes every vertex, edge, and index of namespace “${namespace}” (the namespace stays registered, empty; other namespaces are untouched). This cannot be undone.`}
        instanceName={namespace}
        endpoint={`${describeEndpoint(ns)} → /ns/${namespace}/*`}
        confirmLabel="Erase namespace"
        onConfirm={() => {
          setAdminConfirming(null);
          erase.mutate();
        }}
        onCancel={() => setAdminConfirming(null)}
      />
      <ConfirmDialog
        open={adminConfirming === "factory-reset"}
        title="Factory reset — Fallen-8-wide"
        description="Drops EVERY non-default namespace (their data is gone; save games remain restore points) and erases “default”. This affects ALL namespaces of this Fallen-8 and cannot be undone."
        instanceName={instance.name}
        endpoint={describeEndpoint(instance)}
        confirmLabel="Reset everything"
        onConfirm={() => {
          setAdminConfirming(null);
          factoryReset.mutate();
        }}
        onCancel={() => setAdminConfirming(null)}
      />
      <ConfirmDialog
        open={adminConfirming === "load-path"}
        title="Load a checkpoint"
        description={`Loading replaces namespace “${namespace}”'s in-memory graph with the checkpoint.`}
        instanceName={namespace}
        endpoint={`${describeEndpoint(ns)} → /ns/${namespace}/*`}
        confirmLabel="Load checkpoint"
        onConfirm={() => {
          setAdminConfirming(null);
          loadCheckpoint.mutate();
        }}
        onCancel={() => setAdminConfirming(null)}
      />
    </div>
  );
}
