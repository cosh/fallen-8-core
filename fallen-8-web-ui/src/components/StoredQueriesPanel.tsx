import { useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useNavigate } from "@tanstack/react-router";
import { useActiveInstance } from "../instances/registry";
import { describeEndpoint } from "../instances/types";
import { deleteStoredQuery, getStoredQuery, listStoredQueries } from "../api/endpoints";
import { getInstanceStore } from "../state/instanceStore";
import { describeStoredSpecification } from "../lib/storedQueries";
import { ConfirmDialog } from "./ConfirmDialog";
import { ErrorBox } from "./ErrorBox";

/**
 * Dashboard · Stored queries (concept spec §5.3): the library's ONE management home —
 * list, read-only source, recompile diagnostics, delete (immutable entries: delete +
 * re-register is the edit flow), and Open-in cross-links that pre-select the entry on
 * the consuming screen. Registration deliberately lives on Path/Subgraph, where
 * fragments can be tested before they are captured.
 */
export function StoredQueriesPanel() {
  const instance = useActiveInstance()!;
  const queryClient = useQueryClient();
  const navigate = useNavigate();
  const [expanded, setExpanded] = useState<string | null>(null);
  const [confirming, setConfirming] = useState<string | null>(null);

  const list = useQuery({
    queryKey: [instance.id, "storedqueries"],
    queryFn: ({ signal }) => listStoredQueries(instance, signal),
  });
  const detail = useQuery({
    queryKey: [instance.id, "storedquery", expanded],
    queryFn: ({ signal }) => getStoredQuery(instance, expanded!, signal),
    enabled: Boolean(expanded),
  });

  const remove = useMutation({
    mutationFn: (name: string) => deleteStoredQuery(instance, name),
    onSuccess: () => {
      setExpanded(null);
      queryClient.invalidateQueries({ queryKey: [instance.id, "storedqueries"] });
    },
  });

  const openInPath = (name: string) => {
    getInstanceStore(instance.id)
      .getState()
      .setPathDraft({ filterSource: "stored", storedQuery: name });
    navigate({ to: "/path" });
  };
  const openInSubgraph = (name: string) => {
    getInstanceStore(instance.id).getState().setSubgraphPrefill({ storedQuery: name });
    navigate({ to: "/subgraphs" });
  };

  const entries = list.data ?? [];
  const preview =
    expanded && detail.data
      ? describeStoredSpecification(detail.data.kind, detail.data.specificationJson)
      : null;

  return (
    <section className="panel">
      <div className="panel-title">
        Stored queries
        <span className="text-fg-faint normal-case">
          registered on Path / Subgraph via “Save as stored query…”
        </span>
      </div>
      {list.isError && (
        <div className="p-3">
          <ErrorBox error={list.error} onRetry={() => list.refetch()} />
        </div>
      )}
      <table className="w-full text-[12px]">
        <thead>
          <tr className="text-fg-faint">
            <th className="table-cell">name</th>
            <th className="table-cell">kind</th>
            <th className="table-cell">state</th>
            <th className="table-cell">registered</th>
            <th className="table-cell w-72">actions</th>
          </tr>
        </thead>
        <tbody>
          {entries.map((entry) => (
            <tr key={entry.name ?? "—"}>
              <td className="table-cell font-semibold">{entry.name}</td>
              <td className="table-cell text-fg-dim">{entry.kind}</td>
              <td
                className={`table-cell ${
                  entry.compileState === "Compiled" ? "text-fg-dim" : "text-warn"
                }`}
              >
                {entry.compileState ?? "—"}
              </td>
              <td className="table-cell text-fg-dim">
                {entry.createdAt ? new Date(entry.createdAt).toLocaleString() : "—"}
              </td>
              <td className="table-cell">
                <div className="flex gap-1">
                  <button
                    type="button"
                    className="btn"
                    onClick={() =>
                      setExpanded(expanded === entry.name ? null : entry.name)
                    }
                  >
                    {expanded === entry.name ? "Hide" : "Source"}
                  </button>
                  {entry.kind === "Path" && (
                    <button
                      type="button"
                      className="btn"
                      onClick={() => openInPath(entry.name!)}
                    >
                      Open in Path
                    </button>
                  )}
                  {entry.kind === "SubGraph" && (
                    <button
                      type="button"
                      className="btn"
                      onClick={() => openInSubgraph(entry.name!)}
                    >
                      Open in Subgraph
                    </button>
                  )}
                  <button
                    type="button"
                    className="btn btn-danger"
                    onClick={() => setConfirming(entry.name!)}
                  >
                    Delete…
                  </button>
                </div>
              </td>
            </tr>
          ))}
          {entries.length === 0 && !list.isError && (
            <tr>
              <td className="table-cell text-fg-faint" colSpan={5}>
                no stored queries on this instance
              </td>
            </tr>
          )}
        </tbody>
      </table>

      {expanded && (
        <div className="border-line space-y-1 border-t p-3" data-testid="stored-query-source">
          {detail.isError && <ErrorBox error={detail.error} />}
          {detail.data?.description && (
            <p className="text-fg-dim text-[12px]">{detail.data.description}</p>
          )}
          {preview?.rows.map((row) => (
            <div key={row.label} className="flex items-center gap-2">
              <span className="text-fg-dim w-44 shrink-0 text-[11px] tracking-wider uppercase">
                {row.label}
              </span>
              <code className="text-fg min-w-0 flex-1 truncate text-[11px]" title={row.fragment}>
                {row.fragment}
              </code>
            </div>
          ))}
          {preview?.note && <p className="text-fg-faint text-[11px]">{preview.note}</p>}
          {detail.data?.compileDiagnostics && (
            <pre className="border-danger/40 text-danger mt-2 rounded border p-2 text-[11px] whitespace-pre-wrap">
              {detail.data.compileDiagnostics}
            </pre>
          )}
        </div>
      )}
      {remove.isError && (
        <div className="px-3 pb-3">
          <ErrorBox error={remove.error} />
        </div>
      )}

      <ConfirmDialog
        open={confirming !== null}
        title={`Delete stored query '${confirming ?? ""}'`}
        description="Entries are immutable — to change one, delete and re-register. Requests referencing this name will 404 afterwards."
        instanceName={instance.name}
        endpoint={describeEndpoint(instance)}
        confirmLabel="Delete stored query"
        onConfirm={() => {
          if (confirming) remove.mutate(confirming);
          setConfirming(null);
        }}
        onCancel={() => setConfirming(null)}
      />
    </section>
  );
}
