// MIT License
//
// PluginsPanel.tsx
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
import type { InstanceConfig } from "../instances/types";
import {
  deletePlugin,
  getPlugin,
  invokeGraphFunction,
  listPlugins,
} from "../api/endpoints";
import type { EdgeREST, GraphFunctionResultREST, VertexREST } from "../api/types";
import { ApiError } from "../api/client";
import { PluginEditor } from "../plugin/PluginEditor";
import { ConfirmDialog } from "./ConfirmDialog";
import { ErrorBox } from "./ErrorBox";
import { Field } from "./Field";
import { ListCapNote } from "./ListCapNote";
import { Truncated } from "./Truncated";
import { DISPLAY_CAP } from "../lib/truncate";
import { SCROLL_ROWS, capList, scrollRows } from "../lib/listCaps";

/**
 * Plugins screen · registry table (feature plugin-registration): the registry's ONE
 * management home — list (name, category, contract, compileState badge), read-only source +
 * recompile diagnostics for a selected/Failed entry, a function runner for a registered
 * graph function, and delete (immutable entries: delete + re-register is the edit flow). The
 * whole-type authoring flow lives in the "Register plugin" editor, the sibling of the
 * stored-query library's inline capture — see StoredQueriesPanel for the shape this mirrors.
 */
export function PluginsPanel() {
  const { instance } = useInstanceStore();
  const queryClient = useQueryClient();
  const [expanded, setExpanded] = useState<string | null>(null);
  const [running, setRunning] = useState<string | null>(null);
  const [confirming, setConfirming] = useState<string | null>(null);
  const [authoring, setAuthoring] = useState(false);

  const list = useQuery({
    queryKey: [instance.id, "plugins"],
    queryFn: ({ signal }) => listPlugins(instance, signal),
  });
  const detail = useQuery({
    queryKey: [instance.id, "plugin", expanded],
    queryFn: ({ signal }) => getPlugin(instance, expanded!, signal),
    enabled: Boolean(expanded),
  });

  const remove = useMutation({
    mutationFn: (name: string) => deletePlugin(instance, name),
    onSuccess: () => {
      setExpanded(null);
      setRunning(null);
      queryClient.invalidateQueries({ queryKey: [instance.id, "plugins"] });
    },
  });

  const entries = list.data ?? [];
  const shownEntries = capList(entries);

  return (
    <section className="panel">
      <div className="panel-title">
        Plugins
        <span className="text-fg-faint normal-case">
          runtime-authored, compile-validated, per namespace
        </span>
        <button
          type="button"
          className="btn btn-accent ml-auto normal-case"
          data-testid="register-plugin"
          onClick={() => setAuthoring(true)}
        >
          Register plugin…
        </button>
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
              <th className="table-cell">category</th>
              <th className="table-cell">contract</th>
              <th className="table-cell">state</th>
              <th className="table-cell">registered</th>
              <th className="table-cell w-80">actions</th>
            </tr>
          </thead>
          <tbody>
            {shownEntries.shown.map((entry) => (
              <tr key={entry.name ?? "—"} data-testid={`plugin-row-${entry.name ?? ""}`}>
                <td className="table-cell font-semibold">
                  <Truncated text={entry.name ?? "—"} max={DISPLAY_CAP.name} />
                </td>
                <td className="table-cell text-fg-dim">{entry.category ?? "—"}</td>
                <td className="table-cell text-fg-dim">{entry.contract ?? "—"}</td>
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
                    {entry.category === "Function" && entry.compileState === "Compiled" && (
                      <button
                        type="button"
                        className="btn"
                        data-testid={`plugin-run-${entry.name ?? ""}`}
                        onClick={() =>
                          setRunning(running === entry.name ? null : entry.name)
                        }
                      >
                        {running === entry.name ? "Close" : "Run…"}
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
            {shownEntries.total === 0 && !list.isError && (
              <tr>
                <td className="table-cell text-fg-faint" colSpan={6}>
                  no plugins registered on this namespace
                </td>
              </tr>
            )}
          </tbody>
        </table>
      </div>
      <ListCapNote shown={shownEntries.shown.length} total={shownEntries.total} />

      {expanded && (
        <div className="border-line space-y-1 border-t p-3" data-testid="plugin-source">
          {detail.isError && <ErrorBox error={detail.error} />}
          {detail.data?.description && (
            <p className="text-fg-dim text-[12px] wrap-break-word">{detail.data.description}</p>
          )}
          {detail.data?.sourceCode && (
            <pre className="border-line text-fg max-h-80 overflow-auto rounded border p-2 text-[11px] whitespace-pre">
              {detail.data.sourceCode}
            </pre>
          )}
          {detail.data?.compileDiagnostics && (
            <pre className="border-danger/40 text-danger mt-2 rounded border p-2 text-[11px] wrap-break-word whitespace-pre-wrap">
              {detail.data.compileDiagnostics}
            </pre>
          )}
        </div>
      )}

      {running && <FunctionRunner instance={instance} name={running} />}

      {remove.isError && (
        <div className="px-3 pb-3">
          <ErrorBox error={remove.error} />
        </div>
      )}

      {authoring && (
        <PluginEditor
          instance={instance}
          onRegistered={(name) => {
            setAuthoring(false);
            setExpanded(name);
          }}
          onCancel={() => setAuthoring(false)}
        />
      )}

      <ConfirmDialog
        open={confirming !== null}
        title={`Delete plugin '${confirming ?? ""}'`}
        description="Entries are immutable — to change one, delete and re-register. A registered algorithm invoked by name, or requests to this function, will fail afterwards."
        instanceName={instance.name}
        endpoint={describeEndpoint(instance)}
        confirmLabel="Delete plugin"
        onConfirm={() => {
          if (confirming) remove.mutate(confirming);
          setConfirming(null);
        }}
        onCancel={() => setConfirming(null)}
      />
    </section>
  );
}

interface ParamRow {
  key: string;
  value: string;
}

/**
 * Runs a registered graph function against the addressed namespace: a key/value string
 * parameter form → POST /plugins/function/{name}/invoke → a compact list of the returned
 * vertices/edges (ids + labels), the projection the server hands back.
 */
function FunctionRunner({ instance, name }: { instance: InstanceConfig; name: string }) {
  const [rows, setRows] = useState<ParamRow[]>([{ key: "", value: "" }]);

  const run = useMutation<GraphFunctionResultREST | null, unknown, void>({
    mutationFn: () => {
      const parameters: Record<string, string> = {};
      for (const row of rows) {
        const k = row.key.trim();
        if (k !== "") parameters[k] = row.value;
      }
      return invokeGraphFunction(instance, name, parameters);
    },
  });

  const setRow = (index: number, patch: Partial<ParamRow>) =>
    setRows((prev) => prev.map((row, i) => (i === index ? { ...row, ...patch } : row)));

  const result = run.data ?? null;

  return (
    <div className="border-line space-y-2 border-t p-3" data-testid="plugin-runner">
      <div className="text-fg-dim text-[11px] tracking-wider uppercase">
        run “{name}” — parameters (string-valued)
      </div>
      <div className="space-y-1">
        {rows.map((row, index) => (
          <div key={index} className="flex items-end gap-2">
            <Field helpKey="pluginParameters" label="key" htmlFor={`param-key-${index}`}>
              <input
                id={`param-key-${index}`}
                data-testid={`param-key-${index}`}
                className="input w-40"
                value={row.key}
                onChange={(e) => setRow(index, { key: e.target.value })}
              />
            </Field>
            <Field helpKey="pluginParameters" label="value" htmlFor={`param-value-${index}`}>
              <input
                id={`param-value-${index}`}
                data-testid={`param-value-${index}`}
                className="input w-56"
                value={row.value}
                onChange={(e) => setRow(index, { value: e.target.value })}
              />
            </Field>
            {rows.length > 1 && (
              <button
                type="button"
                className="btn"
                onClick={() => setRows((prev) => prev.filter((_, i) => i !== index))}
              >
                Remove
              </button>
            )}
          </div>
        ))}
      </div>
      <div className="flex items-center gap-2">
        <button
          type="button"
          className="btn"
          onClick={() => setRows((prev) => [...prev, { key: "", value: "" }])}
        >
          Add parameter
        </button>
        <button
          type="button"
          className="btn btn-accent ml-auto"
          data-testid="plugin-invoke"
          disabled={run.isPending}
          onClick={() => run.mutate()}
        >
          {run.isPending ? "Running…" : "Run"}
        </button>
      </div>

      {run.isError && (
        <div className="space-y-1">
          <ErrorBox error={run.error} />
          {run.error instanceof ApiError && run.error.status === 409 && (
            <p className="text-fg-dim text-[12px]">
              This function is not in a runnable (Compiled) state — inspect its diagnostics
              via Source, or delete and re-register.
            </p>
          )}
        </div>
      )}

      {result && <GraphFunctionResultView result={result} />}
    </div>
  );
}

function GraphFunctionResultView({ result }: { result: GraphFunctionResultREST }) {
  const vertices = result.vertices ?? [];
  const edges = result.edges ?? [];
  return (
    <div className="grid grid-cols-1 gap-3 md:grid-cols-2" data-testid="plugin-result">
      <div>
        <div className="text-fg-faint text-[11px] tracking-wider uppercase">
          vertices ({vertices.length})
        </div>
        <ul className="mt-1 space-y-0.5 text-[12px]">
          {vertices.length === 0 && <li className="text-fg-faint">none</li>}
          {vertices.map((v: VertexREST) => (
            <li key={v.id} className="text-fg-dim">
              <span className="text-accent-2">#{v.id}</span>{" "}
              <Truncated text={v.label ?? "—"} max={DISPLAY_CAP.label} />
            </li>
          ))}
        </ul>
      </div>
      <div>
        <div className="text-fg-faint text-[11px] tracking-wider uppercase">
          edges ({edges.length})
        </div>
        <ul className="mt-1 space-y-0.5 text-[12px]">
          {edges.length === 0 && <li className="text-fg-faint">none</li>}
          {edges.map((e: EdgeREST) => (
            <li key={e.id} className="text-fg-dim">
              <span className="text-accent-2">#{e.id}</span>{" "}
              <Truncated text={e.label ?? "—"} max={DISPLAY_CAP.label} />{" "}
              <span className="text-fg-faint">
                ({e.sourceVertex} → {e.targetVertex})
              </span>
            </li>
          ))}
        </ul>
      </div>
    </div>
  );
}
