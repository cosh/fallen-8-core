// MIT License
//
// StoredQueriesPanel.tsx
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
import { useInstanceStore } from "../instances/registry";
import { describeEndpoint } from "../instances/types";
import { deleteStoredQuery, getStoredQuery, listStoredQueries } from "../api/endpoints";
import type { StoredQuerySummaryREST } from "../api/types";
import { describeStoredSpecification } from "../lib/storedQueries";
import { ConfirmDialog } from "./ConfirmDialog";
import { ErrorBox } from "./ErrorBox";
import { ListCapNote } from "./ListCapNote";
import { Truncated } from "./Truncated";
import { DISPLAY_CAP } from "../lib/truncate";
import { SCROLL_ROWS, capList, scrollRows } from "../lib/listCaps";

/** Columns of the library table, for the empty row's span. */
const COLUMN_COUNT = 5;

/**
 * The stored-query library (concept spec §5.3): the WHOLE `/storedquery` collection of the
 * namespace in one table, with a **kind** column naming each entry's scenario. It was always
 * one server-side collection; until feature studio-traverse-merge the Studio rendered it as
 * two kind-scoped panels, one per scenario screen, which hid that fact and split the view.
 *
 * Read-only source, recompile diagnostics, delete (entries are immutable, so delete plus
 * re-register is the edit flow), and a **Use** action that hands the entry back to the host,
 * which selects it into the matching scenario's filter picker. Capture ("Save as stored
 * query…") stays in each scenario's inline advanced tier, next to the fragments it captures.
 */
export function StoredQueriesPanel({
  onUse,
}: {
  /**
   * Select this entry into its scenario's filter picker (stored source + name). The whole
   * entry is handed over, not just the name, because the host has to route it by `kind`.
   */
  onUse: (entry: StoredQuerySummaryREST) => void;
}) {
  const { instance } = useInstanceStore();
  const queryClient = useQueryClient();
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

  const shownEntries = capList(list.data ?? []);
  const preview =
    expanded && detail.data
      ? describeStoredSpecification(detail.data.kind, detail.data.specificationJson)
      : null;

  return (
    <section className="panel" data-testid="stored-queries-all">
      <div className="panel-title">
        Stored queries
        <span className="text-fg-faint normal-case">
          registered via “Save as stored query…”
        </span>
      </div>
      {list.isError && (
        <div className="p-3">
          <ErrorBox error={list.error} onRetry={() => list.refetch()} />
        </div>
      )}
      <div className="scroll-list" style={scrollRows(SCROLL_ROWS.default)}>
      <table className="w-full text-[12px]">
        <thead>
          <tr className="text-fg-faint">
            <th className="table-cell">name</th>
            <th className="table-cell">kind</th>
            <th className="table-cell">state</th>
            <th className="table-cell">registered</th>
            <th className="table-cell w-64">actions</th>
          </tr>
        </thead>
        <tbody>
          {shownEntries.shown.map((entry) => (
            <tr key={entry.name ?? "—"}>
              <td className="table-cell font-semibold">
                <Truncated text={entry.name ?? "—"} max={DISPLAY_CAP.name} />
              </td>
              <td
                className="table-cell text-fg-dim"
                data-testid={`stored-query-kind-${entry.name}`}
              >
                {entry.kind ?? "—"}
              </td>
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
                  <button
                    type="button"
                    className="btn"
                    data-testid={`stored-query-use-${entry.name}`}
                    disabled={entry.compileState === "Failed"}
                    title={
                      entry.compileState === "Failed"
                        ? "recompile failed on this instance — delete and re-register"
                        : "select it into its scenario's filter picker"
                    }
                    onClick={() => onUse(entry)}
                  >
                    Use
                  </button>
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
          {shownEntries.total === 0 && !list.isError && (
            <tr>
              <td className="table-cell text-fg-faint" colSpan={COLUMN_COUNT}>
                no stored queries on this instance: author fragments on the Path finding or
                Subgraph builder tab, then “Save as stored query…”
              </td>
            </tr>
          )}
        </tbody>
      </table>
      </div>
      <ListCapNote shown={shownEntries.shown.length} total={shownEntries.total} />

      {expanded && (
        <div className="border-line space-y-1 border-t p-3" data-testid="stored-query-source">
          {detail.isError && <ErrorBox error={detail.error} />}
          {detail.data?.description && (
            <p className="text-fg-dim text-[12px] wrap-break-word">{detail.data.description}</p>
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
            <pre className="border-danger/40 text-danger mt-2 rounded border p-2 text-[11px] wrap-break-word whitespace-pre-wrap">
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
