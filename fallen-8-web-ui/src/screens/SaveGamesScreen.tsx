import { useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useActiveInstance } from "../instances/registry";
import { describeEndpoint } from "../instances/types";
import {
  deleteSaveGame,
  listSaveGames,
  loadSaveGame,
  saveAllNamespaces,
} from "../api/endpoints";
import type { SaveGame, SaveGameNamespace } from "../api/types";
import { formatExact } from "../lib/format";
import { ErrorBox } from "../components/ErrorBox";
import { ConfirmDialog } from "../components/ConfirmDialog";
import { help } from "../lib/fieldHelp";

/**
 * Save games (feature save-games + graph-namespaces): the persistent checkpoint registry
 * as a table. Fallen-8-level — an entry can span several namespaces ("Save all" creates
 * one), and loading restores exactly the namespaces an entry contains (or one of them,
 * via the restore selector). Sits under Dashboard in the rail.
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
  const instance = useActiveInstance()!;
  const queryClient = useQueryClient();
  const [message, setMessage] = useState<string | null>(null);
  const [confirming, setConfirming] = useState<
    { kind: "load" | "delete"; game: SaveGame } | null
  >(null);
  const [deleteFiles, setDeleteFiles] = useState(false);
  /** "" = restore the entire entry; otherwise the one namespace to restore. */
  const [loadNamespace, setLoadNamespace] = useState("");

  const list = useQuery({
    queryKey: [instance.id, "savegames"],
    queryFn: ({ signal }) => listSaveGames(instance, signal),
  });

  const invalidate = () => queryClient.invalidateQueries({ queryKey: [instance.id] });

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

  const failed = [saveAll, load, remove].find((m) => m.isError);

  const confirmingMembers = confirming ? effectiveNamespaces(confirming.game) : [];

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
        <div className="overflow-x-auto">
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
              {(list.data ?? []).map((game) => {
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
              {(list.data ?? []).length === 0 && !list.isError && (
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
      </section>

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
    </div>
  );
}
