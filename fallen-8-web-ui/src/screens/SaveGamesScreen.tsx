import { useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useActiveInstance } from "../instances/registry";
import { describeEndpoint } from "../instances/types";
import {
  deleteSaveGame,
  listSaveGames,
  loadSaveGame,
  saveGraph,
} from "../api/endpoints";
import type { SaveGame } from "../api/types";
import { formatExact } from "../lib/format";
import { ErrorBox } from "../components/ErrorBox";
import { ConfirmDialog } from "../components/ConfirmDialog";
import { help } from "../lib/fieldHelp";

/**
 * Save games (feature save-games): the persistent checkpoint registry as a table, with
 * Save now, Load (typed confirmation - loading replaces the graph), and Delete (typed
 * confirmation, optional file deletion). Sits under Dashboard in the rail.
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

export function SaveGamesScreen() {
  const instance = useActiveInstance()!;
  const queryClient = useQueryClient();
  const [message, setMessage] = useState<string | null>(null);
  const [confirming, setConfirming] = useState<
    { kind: "load" | "delete"; game: SaveGame } | null
  >(null);
  const [deleteFiles, setDeleteFiles] = useState(false);

  const list = useQuery({
    queryKey: [instance.id, "savegames"],
    queryFn: ({ signal }) => listSaveGames(instance, signal),
  });

  const invalidate = () => queryClient.invalidateQueries({ queryKey: [instance.id] });

  const saveNow = useMutation({
    mutationFn: () => saveGraph(instance),
    onSuccess: (entry) => {
      setMessage(
        entry
          ? `Saved: ${entry.kpis.vertexCount} vertices, ${entry.kpis.edgeCount} edges → ${entry.location}`
          : "Saved.",
      );
      invalidate();
    },
  });

  const load = useMutation({
    mutationFn: (id: string) => loadSaveGame(instance, id),
    onSuccess: (entry) => {
      setMessage(entry ? `Loaded save game ${entry.id}.` : "Loaded.");
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

  const failed = [saveNow, load, remove].find((m) => m.isError);

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
          disabled={saveNow.isPending}
          onClick={() => saveNow.mutate()}
        >
          {saveNow.isPending ? "Saving…" : "Save now"}
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
            metadata/savegames.json · values captured at save time
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
                <th className="table-cell">vertices</th>
                <th className="table-cell">edges</th>
                <th className="table-cell">files</th>
                <th className="table-cell">size</th>
                <th className="table-cell">indices / subgraphs</th>
                <th className="table-cell">location</th>
                <th className="table-cell w-40">actions</th>
              </tr>
            </thead>
            <tbody>
              {(list.data ?? []).map((game) => (
                <tr key={game.id} data-testid={`savegame-row-${game.id}`} className="hover:bg-panel-2">
                  <td className="table-cell">{formatSavedAt(game.savedAt)}</td>
                  <td className="table-cell">
                    <span className="border-line rounded border px-1.5 py-0.5 text-[10px] tracking-wider uppercase">
                      {game.trigger}
                    </span>
                  </td>
                  <td className="table-cell">{formatExact(game.kpis.vertexCount)}</td>
                  <td className="table-cell">{formatExact(game.kpis.edgeCount)}</td>
                  <td className="table-cell">{game.fileCount}</td>
                  <td className="table-cell">{formatBytes(game.totalBytes)}</td>
                  <td className="table-cell text-fg-dim">
                    {game.kpis.indices.map((i) => i.indexId).join(", ") || "—"}
                    {game.kpis.subGraphs.length > 0 && (
                      <div className="text-fg-faint">sg: {game.kpis.subGraphs.join(", ")}</div>
                    )}
                  </td>
                  <td className="table-cell text-fg-faint max-w-64 truncate" title={game.location}>
                    {game.location}
                  </td>
                  <td className="table-cell">
                    <div className="flex gap-1">
                      <button
                        type="button"
                        className="btn"
                        data-testid={`load-${game.id}`}
                        onClick={() => setConfirming({ kind: "load", game })}
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
              ))}
              {(list.data ?? []).length === 0 && !list.isError && (
                <tr>
                  <td className="table-cell text-fg-faint" colSpan={9}>
                    No save games yet. “Save now” creates the first one; loading a checkpoint on
                    another instance registers it automatically.
                  </td>
                </tr>
              )}
            </tbody>
          </table>
        </div>
      </section>

      <ConfirmDialog
        open={confirming?.kind === "load"}
        title="Load save game"
        description="Loading replaces the entire in-memory graph with this save game."
        instanceName={instance.name}
        endpoint={describeEndpoint(instance)}
        confirmLabel="Load save game"
        onConfirm={() => {
          const game = confirming!.game;
          setConfirming(null);
          load.mutate(game.id);
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
