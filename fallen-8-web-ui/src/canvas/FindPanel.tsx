// MIT License
//
// FindPanel.tsx
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
import { useMutation } from "@tanstack/react-query";
import { useInstanceStore } from "../instances/registry";
import { shapeSuggestions, useGraphShape } from "../state/graphShape";
import { scanProperties } from "../api/endpoints";
import type { EdgeREST, VertexREST } from "../api/types";
import { hydrateElements, isEdge, type HydrationProgress } from "../lib/hydrate";
import { SCROLL_ROWS, capList, scrollRows } from "../lib/listCaps";
import { DISPLAY_CAP } from "../lib/truncate";
import type { ElementRef } from "./GraphCanvas";
import { Field } from "../components/Field";
import { Truncated } from "../components/Truncated";
import { ListCapNote } from "../components/ListCapNote";
import { ErrorBox } from "../components/ErrorBox";

const RESULT_TYPES = ["Vertices", "Edges", "Both"] as const;

/**
 * Canvas "Find" tab (feature canvas-find-connect): the all-property discovery search
 * (POST /scan/graph/properties, the same primitive the Query screen's "any property" scope
 * uses) brought onto the canvas so you can grow the working set without leaving. Results show
 * whether each element is already on the canvas and add it (or all of them) in one click; a row
 * click selects the element into the Detail panel for the full property view. Inputs persist in
 * the per-instance canvasToolsDraft; results are ephemeral (re-run on demand).
 */
export function FindPanel({ onSelect }: { onSelect: (ref: ElementRef) => void }) {
  const { instance, store } = useInstanceStore();
  const draft = store((s) => s.canvasToolsDraft);
  const setDraft = store((s) => s.setCanvasToolsDraft);
  const mergeIntoCanvas = store((s) => s.mergeIntoCanvas);
  const canvasNodes = store((s) => s.canvasNodes);
  const canvasEdges = store((s) => s.canvasEdges);
  const { findTerm, findLabel, findResultType } = draft;

  const suggestions = shapeSuggestions(useGraphShape(instance).data);
  const labelOptions = [...new Set([...suggestions.vertexLabels, ...suggestions.edgeLabels])];

  const [elements, setElements] = useState<(VertexREST | EdgeREST)[]>([]);
  const [idCount, setIdCount] = useState<number | null>(null);
  const [capped, setCapped] = useState(false);
  const [progress, setProgress] = useState<HydrationProgress | null>(null);

  const search = useMutation({
    mutationFn: async () => {
      setElements([]);
      setIdCount(null);
      setCapped(false);
      setProgress(null);
      const ids =
        (await scanProperties(instance, {
          searchTerm: findTerm,
          label: findLabel || undefined,
          resultType: findResultType,
        })) ?? [];
      setIdCount(ids.length);
      const hydrated = await hydrateElements(instance, ids, { onProgress: setProgress });
      setCapped(hydrated.capped);
      return hydrated.elements;
    },
    onSuccess: (hydrated) => setElements(hydrated),
    onSettled: () => setProgress(null),
  });

  // "Already on the canvas" must not count a stub: buildCanvasModel synthesizes a placeholder
  // node (no `props`) for an edge's unloaded endpoint, so a real vertex whose only presence is
  // such a stub still offers "+ canvas" to hydrate it. Real merged vertices always carry `props`
  // (snapshotProps returns an object); edges are never stubbed, so their presence is exact.
  const onCanvas = (el: VertexREST | EdgeREST): boolean =>
    isEdge(el)
      ? canvasEdges[el.id] !== undefined
      : canvasNodes[el.id]?.props !== undefined;

  const add = (el: VertexREST | EdgeREST) =>
    isEdge(el) ? mergeIntoCanvas([], [el]) : mergeIntoCanvas([el], []);

  const sendAll = () =>
    mergeIntoCanvas(
      elements.filter((e): e is VertexREST => !isEdge(e)),
      elements.filter(isEdge),
    );

  const { shown, total } = capList(elements);
  const termBlank = !findTerm.trim();

  return (
    <div data-testid="find-panel" className="space-y-3 p-3 text-[12px]">
      <datalist id="find-labels">
        {labelOptions.map((label) => (
          <option key={label} value={label} />
        ))}
      </datalist>

      <form
        className="space-y-2"
        onSubmit={(e) => {
          e.preventDefault();
          if (!termBlank) search.mutate();
        }}
      >
        <Field helpKey="searchTerm" label="search term" htmlFor="find-term">
          <input
            id="find-term"
            data-testid="find-term"
            className="input"
            value={findTerm}
            onChange={(e) => setDraft({ findTerm: e.target.value })}
            placeholder="acme"
          />
        </Field>
        <div className="flex gap-2">
          <Field helpKey="searchLabel" label="label" htmlFor="find-label" className="min-w-0 flex-1">
            <input
              id="find-label"
              data-testid="find-label"
              className="input"
              list="find-labels"
              value={findLabel}
              onChange={(e) => setDraft({ findLabel: e.target.value })}
              placeholder="any label"
            />
          </Field>
          <Field helpKey="scanResultType" label="result" htmlFor="find-result-type">
            <select
              id="find-result-type"
              data-testid="find-result-type"
              className="input w-auto"
              value={findResultType}
              onChange={(e) =>
                setDraft({ findResultType: e.target.value as (typeof RESULT_TYPES)[number] })
              }
            >
              {RESULT_TYPES.map((rt) => (
                <option key={rt}>{rt}</option>
              ))}
            </select>
          </Field>
        </div>
        <button
          type="submit"
          className="btn btn-accent w-full"
          data-testid="find-run"
          disabled={search.isPending || termBlank}
        >
          {search.isPending ? "Searching…" : "Find"}
        </button>
      </form>

      {progress && (
        <div className="text-fg-dim" data-testid="find-progress">
          hydrating {progress.done}/{progress.total}…
        </div>
      )}
      {search.isError && <ErrorBox error={search.error} />}

      {idCount !== null && !search.isPending && (
        <div className="space-y-1">
          <div className="flex items-center gap-2">
            <span className="text-fg-dim" data-testid="find-count">
              {idCount} match{idCount === 1 ? "" : "es"}
              {capped && <span className="text-warn"> (first 500 shown)</span>}
            </span>
            <button
              type="button"
              className="btn btn-accent ml-auto"
              data-testid="find-send-all"
              disabled={elements.length === 0}
              onClick={sendAll}
            >
              Send all
            </button>
          </div>

          {elements.length === 0 ? (
            <div className="text-fg-faint">No elements.</div>
          ) : (
            <div className="scroll-list" style={scrollRows(SCROLL_ROWS.default)}>
              {shown.map((el) => {
                const here = onCanvas(el);
                return (
                  <div
                    key={el.id}
                    data-testid={`find-row-${el.id}`}
                    className="hover:bg-panel-2 flex items-center gap-1.5 px-1 py-0.5"
                  >
                    <span className="text-fg-faint w-3 shrink-0" title={isEdge(el) ? "edge" : "vertex"}>
                      {isEdge(el) ? "e" : "v"}
                    </span>
                    <button
                      type="button"
                      className="text-accent-2 shrink-0 cursor-pointer hover:underline"
                      title="Show details below"
                      onClick={() => onSelect({ kind: isEdge(el) ? "edge" : "node", id: el.id })}
                    >
                      #{el.id}
                    </button>
                    <Truncated
                      text={el.label ?? "—"}
                      max={DISPLAY_CAP.label}
                      className="text-fg-dim min-w-0 flex-1"
                    />
                    {here ? (
                      <span
                        className="text-fg-faint shrink-0"
                        data-testid={`find-oncanvas-${el.id}`}
                        title="Already on the canvas"
                      >
                        ● on canvas
                      </span>
                    ) : (
                      <button
                        type="button"
                        data-testid={`find-add-${el.id}`}
                        className="text-accent shrink-0 cursor-pointer hover:underline"
                        title="Add this element to the canvas"
                        onClick={() => add(el)}
                      >
                        + canvas
                      </button>
                    )}
                  </div>
                );
              })}
              <ListCapNote shown={shown.length} total={total} />
            </div>
          )}
        </div>
      )}
    </div>
  );
}
