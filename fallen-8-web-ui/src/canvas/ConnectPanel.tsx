// MIT License
//
// ConnectPanel.tsx
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

import { useMemo, useRef, useState } from "react";
import { useMutation } from "@tanstack/react-query";
import { useInstanceStore } from "../instances/registry";
import { findPaths, getGraphElement } from "../api/endpoints";
import type { PathREST, VertexREST } from "../api/types";
import { isEdge } from "../lib/hydrate";
import {
  CONNECT_BATCH_SIZE,
  CONNECT_PAIR_CAP,
  buildPairs,
  introducedSets,
  pairCount,
  removalSet,
  synthesizeEdges,
  type CanvasBaseline,
  type IntroducedSets,
} from "../lib/connectPaths";
import { SCROLL_ROWS, capList, scrollRows } from "../lib/listCaps";
import { DISPLAY_CAP } from "../lib/truncate";
import { Field } from "../components/Field";
import { Truncated } from "../components/Truncated";
import { ListCapNote } from "../components/ListCapNote";
import { ErrorBox } from "../components/ErrorBox";

/** One found connection: the pair, its shortest path, and what that path adds over the run baseline. */
interface FoundRow {
  pair: [number, number];
  path: PathREST;
  introduced: IntroducedSets;
  hops: number;
}

/** Progress + tally of a completed (or cancelled) run. */
interface RunSummary {
  found: number;
  unreachable: number;
  failed: number;
  total: number;
  done: number;
  cancelled: boolean;
  /** The hop bound this run actually used, so editing the input afterwards can't rewrite it. */
  maxDepth: number;
}

type PairOutcome =
  | { pair: [number, number]; status: "ok"; paths: PathREST[] | null }
  | { pair: [number, number]; status: "aborted" }
  | { pair: [number, number]; status: "error" };

const pairKey = (pair: [number, number]): string => `${pair[0]}-${pair[1]}`;

/**
 * Canvas "Connect" tab (feature canvas-find-connect): connect the vertices already on the canvas
 * by running Fallen-8's shortest-path search (BLS) over every unordered pair, up to a max-hop
 * bound, then add or retract each found path selectively. The whole pairwise sweep must fit under
 * CONNECT_PAIR_CAP - a partial sweep would falsely report "unreachable" for pairs it never tried,
 * so above the cap the run is refused and the pick list is the way to narrow. Add/remove is
 * reference-counted against the run baseline (connectPaths.ts): a shared intermediate survives
 * until the last path claiming it is removed. Inputs persist; picks and results are ephemeral.
 */
export function ConnectPanel() {
  const { instance, store } = useInstanceStore();
  const draft = store((s) => s.canvasToolsDraft);
  const setDraft = store((s) => s.setCanvasToolsDraft);
  const canvasNodes = store((s) => s.canvasNodes);
  const canvasEdges = store((s) => s.canvasEdges);
  const mergeIntoCanvas = store((s) => s.mergeIntoCanvas);
  const removeFromCanvas = store((s) => s.removeFromCanvas);
  const { connectMaxDepth, connectScope } = draft;

  const [picked, setPicked] = useState<Set<number>>(new Set());
  const [pickFilter, setPickFilter] = useState("");
  const [rows, setRows] = useState<FoundRow[]>([]);
  const [added, setAdded] = useState<Set<string>>(new Set());
  const [progress, setProgress] = useState<{ done: number; total: number } | null>(null);
  const [summary, setSummary] = useState<RunSummary | null>(null);
  const controllerRef = useRef<AbortController | null>(null);

  const nodeList = useMemo(() => Object.values(canvasNodes), [canvasNodes]);
  const filteredNodes = useMemo(() => {
    const f = pickFilter.trim().toLowerCase();
    if (!f) return nodeList;
    return nodeList.filter(
      (n) => String(n.id).includes(f) || (n.label ?? "").toLowerCase().includes(f),
    );
  }, [nodeList, pickFilter]);

  // The endpoint set for THIS run, restricted to vertices actually on the canvas.
  const endpointIds = useMemo(() => {
    if (connectScope === "all") return nodeList.map((n) => n.id);
    return nodeList.map((n) => n.id).filter((id) => picked.has(id));
  }, [connectScope, nodeList, picked]);

  const pairs = pairCount(endpointIds.length);
  const overCap = pairs > CONNECT_PAIR_CAP;
  const tooFew = endpointIds.length < 2;

  const run = useMutation({
    mutationFn: async () => {
      const controller = new AbortController();
      controllerRef.current = controller;
      const baseline: CanvasBaseline = {
        nodes: new Set(Object.keys(canvasNodes).map(Number)),
        edges: new Set(Object.keys(canvasEdges).map(Number)),
      };
      const allPairs = buildPairs(endpointIds);
      setRows([]);
      setAdded(new Set());
      setSummary(null);
      setProgress({ done: 0, total: allPairs.length });

      const found: FoundRow[] = [];
      let unreachable = 0;
      let failed = 0;
      let done = 0;
      const spec = {
        pathAlgorithmName: "BLS",
        maxDepth: connectMaxDepth,
        maxResults: 1,
        maxPathWeight: Number.MAX_VALUE,
      };

      for (let i = 0; i < allPairs.length; i += CONNECT_BATCH_SIZE) {
        if (controller.signal.aborted) break;
        const batch = allPairs.slice(i, i + CONNECT_BATCH_SIZE);
        const outcomes = await Promise.all(
          batch.map(async ([a, b]): Promise<PairOutcome> => {
            try {
              const paths = await findPaths(instance, a, b, spec, controller.signal);
              return { pair: [a, b], status: "ok", paths };
            } catch {
              return {
                pair: [a, b],
                status: controller.signal.aborted ? "aborted" : "error",
              };
            }
          }),
        );
        for (const outcome of outcomes) {
          if (outcome.status === "aborted") continue;
          if (outcome.status === "error") {
            failed++;
            continue;
          }
          const path = outcome.paths && outcome.paths.length > 0 ? outcome.paths[0] : null;
          if (!path) {
            unreachable++;
            continue;
          }
          found.push({
            pair: outcome.pair,
            path,
            introduced: introducedSets(path, baseline),
            hops: path.pathElements.length,
          });
        }
        done = Math.min(i + batch.length, allPairs.length);
        setProgress({ done, total: allPairs.length });
        setRows([...found]);
      }

      setSummary({
        found: found.length,
        unreachable,
        failed,
        total: allPairs.length,
        done,
        cancelled: controller.signal.aborted,
        maxDepth: connectMaxDepth,
      });
    },
    onSettled: () => {
      setProgress(null);
      controllerRef.current = null;
    },
  });

  const addRow = async (row: FoundRow) => {
    const vertexIds = [...row.introduced.nodeIds];
    const vertices = (
      await Promise.all(vertexIds.map((id) => getGraphElement(instance, id).catch(() => null)))
    ).filter((v): v is VertexREST => v !== null && !isEdge(v));
    const edges = synthesizeEdges(row.path).filter((e) => row.introduced.edgeIds.has(e.id));
    mergeIntoCanvas(vertices, edges);
    setAdded((prev) => new Set(prev).add(pairKey(row.pair)));
  };

  const removeRow = (row: FoundRow) => {
    const key = pairKey(row.pair);
    // Only OTHER still-added paths keep a shared element alive.
    const others = rows
      .filter((r) => r !== row && added.has(pairKey(r.pair)))
      .map((r) => r.introduced);
    const toRemove = removalSet(row.introduced, others);
    // Read the LIVE canvas, not the run-time baseline: the user may have merged more since the
    // run (Show whole graph / Expand neighbors), turning an introduced intermediate into a
    // first-class vertex with its own edges. Remove this path's introduced edges, but keep any
    // introduced node that still has an edge on the canvas - otherwise removeFromCanvas("node")
    // would cascade-drop every edge incident to it, including those an external merge attached.
    const liveEdges = Object.values(store.getState().canvasEdges);
    for (const id of toRemove.edgeIds) removeFromCanvas("edge", id);
    const survivingEdges = liveEdges.filter((e) => !toRemove.edgeIds.has(e.id));
    for (const id of toRemove.nodeIds) {
      const stillConnected = survivingEdges.some((e) => e.source === id || e.target === id);
      if (!stillConnected) removeFromCanvas("node", id);
    }
    setAdded((prev) => {
      const next = new Set(prev);
      next.delete(key);
      return next;
    });
  };

  const addAll = async () => {
    for (const row of rows) {
      if (!added.has(pairKey(row.pair))) await addRow(row);
    }
  };

  const togglePick = (id: number) =>
    setPicked((prev) => {
      const next = new Set(prev);
      if (next.has(id)) next.delete(id);
      else next.add(id);
      return next;
    });

  const { shown: shownNodes, total: totalNodes } = capList(filteredNodes);

  return (
    <div data-testid="connect-panel" className="space-y-3 p-3 text-[12px]">
      <p className="text-fg-faint text-[11px]">
        Connect the vertices on this canvas: a shortest path is searched for each pair and added
        to the view only when you keep it. The database is never touched.
      </p>

      <Field helpKey="connectScope" label="endpoints" htmlFor="connect-scope">
        <div className="border-line flex overflow-hidden rounded border">
          {(["all", "pick"] as const).map((scope) => (
            <button
              key={scope}
              type="button"
              data-testid={`connect-scope-${scope}`}
              className={`px-2 py-1 text-[11px] ${
                connectScope === scope ? "bg-panel-2 text-accent" : "text-fg-dim hover:text-fg"
              }`}
              onClick={() => setDraft({ connectScope: scope })}
            >
              {scope === "all" ? "all vertices" : "pick vertices"}
            </button>
          ))}
        </div>
      </Field>

      {connectScope === "pick" && (
        <div className="space-y-1">
          <input
            data-testid="connect-pick-filter"
            className="input"
            value={pickFilter}
            onChange={(e) => setPickFilter(e.target.value)}
            placeholder="filter by id or label"
          />
          {nodeList.length === 0 ? (
            <div className="text-fg-faint">No vertices on the canvas yet.</div>
          ) : (
            <div className="scroll-list" style={scrollRows(SCROLL_ROWS.default)}>
              {shownNodes.map((n) => (
                <label
                  key={n.id}
                  className="hover:bg-panel-2 flex items-center gap-1.5 px-1 py-0.5"
                  data-testid={`connect-pick-${n.id}`}
                >
                  <input
                    type="checkbox"
                    checked={picked.has(n.id)}
                    onChange={() => togglePick(n.id)}
                  />
                  <span className="text-fg-faint shrink-0">#{n.id}</span>
                  <Truncated
                    text={n.label ?? "—"}
                    max={DISPLAY_CAP.label}
                    className="text-fg-dim min-w-0 flex-1"
                  />
                </label>
              ))}
              <ListCapNote shown={shownNodes.length} total={totalNodes} />
            </div>
          )}
        </div>
      )}

      <Field helpKey="pathMaxDepth" label="max hops" htmlFor="connect-max-hops">
        <input
          id="connect-max-hops"
          data-testid="connect-max-hops"
          type="number"
          min={1}
          className="input w-24"
          value={connectMaxDepth}
          onChange={(e) => setDraft({ connectMaxDepth: Math.max(1, Number(e.target.value) || 1) })}
        />
      </Field>

      <div className="text-fg-dim" data-testid="connect-pair-count">
        {endpointIds.length} vertices → {pairs} pair{pairs === 1 ? "" : "s"}
      </div>
      {overCap && (
        <div className="text-warn" data-testid="connect-over-cap">
          Over the {CONNECT_PAIR_CAP}-pair limit. Pick fewer vertices to run.
        </div>
      )}

      <div className="flex gap-2">
        <button
          type="button"
          className="btn btn-accent"
          data-testid="connect-run"
          disabled={run.isPending || tooFew || overCap}
          onClick={() => run.mutate()}
        >
          {run.isPending ? "Searching…" : "Find connections"}
        </button>
        {run.isPending && (
          <button
            type="button"
            className="btn"
            data-testid="connect-cancel"
            onClick={() => controllerRef.current?.abort()}
          >
            Cancel
          </button>
        )}
      </div>

      {progress && (
        <div className="text-fg-dim" data-testid="connect-progress">
          pair {progress.done}/{progress.total}…
        </div>
      )}
      {run.isError && <ErrorBox error={run.error} />}

      {summary && (
        <div className="text-fg-dim" data-testid="connect-summary">
          {summary.found} connection{summary.found === 1 ? "" : "s"} found ·{" "}
          {summary.unreachable} unreachable within {summary.maxDepth} hops
          {summary.failed > 0 && ` · ${summary.failed} failed`}
          {summary.cancelled && ` · cancelled after ${summary.done} of ${summary.total} pairs`}
        </div>
      )}

      {rows.length > 0 && (
        <div className="space-y-1">
          <div className="flex items-center">
            <span className="text-fg-faint text-[10px] tracking-widest uppercase">connections</span>
            <button
              type="button"
              className="btn btn-accent ml-auto"
              data-testid="connect-add-all"
              disabled={rows.every((r) => added.has(pairKey(r.pair)))}
              onClick={addAll}
            >
              Add all
            </button>
          </div>
          <div className="scroll-list" style={scrollRows(SCROLL_ROWS.default)}>
            {rows.map((row) => {
              const key = pairKey(row.pair);
              const isAdded = added.has(key);
              const newCount = row.introduced.nodeIds.size + row.introduced.edgeIds.size;
              return (
                <div
                  key={key}
                  data-testid={`connect-row-${key}`}
                  className="hover:bg-panel-2 flex items-center gap-1.5 px-1 py-0.5"
                >
                  <span className="text-fg-dim shrink-0">
                    #{row.pair[0]} → #{row.pair[1]}
                  </span>
                  <span className="text-fg-faint min-w-0 flex-1">
                    {row.hops} hop{row.hops === 1 ? "" : "s"}, {newCount} new
                  </span>
                  <button
                    type="button"
                    data-testid={`connect-toggle-${key}`}
                    className={`shrink-0 cursor-pointer hover:underline ${
                      isAdded ? "text-warn" : "text-accent"
                    }`}
                    onClick={() => (isAdded ? removeRow(row) : addRow(row))}
                  >
                    {isAdded ? "remove" : "add"}
                  </button>
                </div>
              );
            })}
          </div>
        </div>
      )}
    </div>
  );
}
