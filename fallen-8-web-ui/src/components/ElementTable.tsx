// MIT License
//
// ElementTable.tsx
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

import type { EdgeREST, VertexREST } from "../api/types";
import { isEdge } from "../lib/hydrate";
import { previewVector, userPropertiesFirst } from "../lib/embeddingProperties";
import { DISPLAY_CAP } from "../lib/truncate";
import { SCROLL_ROWS, scrollRows } from "../lib/listCaps";
import { Truncated } from "./Truncated";

/**
 * Hydrated element list as a table (FR-11 "open as table"). `scores` adds the optional
 * score column shared by the vector scan and analytics top-K (concept spec §9);
 * `scoreHeader` names the metric so an L2 distance is never misread as a similarity.
 */
export function ElementTable({
  elements,
  onInspect,
  onAddToCanvas,
  scores,
  scoreHeader = "score",
}: {
  elements: (VertexREST | EdgeREST)[];
  onInspect?: (id: number) => void;
  /**
   * Optional per-row "add just this element to the canvas" action (feature: per-row canvas add).
   * When provided, a trailing action column renders one button per row; the caller maps the
   * element to the canvas merge (a lone edge brings its endpoints in as placeholder nodes). Callers
   * that do not pass it (Browser, Analytics) render no extra column - the addition is opt-in.
   */
  onAddToCanvas?: (element: VertexREST | EdgeREST) => void;
  scores?: Map<number, number>;
  scoreHeader?: string;
}) {
  if (elements.length === 0) {
    return <div className="text-fg-faint p-3 text-[12px]">No elements.</div>;
  }
  return (
    // Height-capped + scrolls (the count is already bounded by every caller — e.g. the Browser's
    // "first 200 shown"); `.scroll-list` keeps a large result set from growing the page.
    <div className="scroll-list" style={scrollRows(SCROLL_ROWS.default)}>
      <table className="w-full text-[12px]">
        <thead>
          <tr className="text-fg-faint">
            <th className="table-cell">id</th>
            {scores && <th className="table-cell">{scoreHeader}</th>}
            <th className="table-cell">kind</th>
            <th className="table-cell">label</th>
            <th className="table-cell">endpoints</th>
            <th className="table-cell">properties</th>
            {onAddToCanvas && <th className="table-cell" />}
          </tr>
        </thead>
        <tbody>
          {elements.map((element) => (
            <tr key={element.id} className="hover:bg-panel-2">
              <td className="table-cell">
                {onInspect ? (
                  <button
                    type="button"
                    className="text-accent-2 cursor-pointer hover:underline"
                    onClick={() => onInspect(element.id)}
                  >
                    {element.id}
                  </button>
                ) : (
                  element.id
                )}
              </td>
              {scores && (
                <td className="table-cell text-fg font-mono">
                  {scores.has(element.id) ? scores.get(element.id)!.toFixed(4) : "—"}
                </td>
              )}
              <td className="table-cell">{isEdge(element) ? "edge" : "vertex"}</td>
              <td className="table-cell">
                <Truncated text={element.label ?? "—"} max={DISPLAY_CAP.label} />
              </td>
              <td className="table-cell text-fg-dim">
                {isEdge(element)
                  ? `${element.sourceVertex} → ${element.targetVertex}`
                  : "—"}
              </td>
              <td className="table-cell text-fg-dim">
                {/* previewVector per value so an embedding never dumps into the join, and the
                    element's OWN properties before the engine's embedding bookkeeping, because the
                    cell truncates and the budget belongs to the operator's data. */}
                <Truncated
                  text={
                    userPropertiesFirst(element.properties ?? [])
                      .map((p) => `${p.propertyId}=${previewVector(p.propertyValue)}`)
                      .join(", ") || "—"
                  }
                  max={DISPLAY_CAP.propertyValue}
                />
              </td>
              {onAddToCanvas && (
                <td className="table-cell">
                  <button
                    type="button"
                    data-testid={`row-to-canvas-${element.id}`}
                    className="text-accent cursor-pointer text-[11px] whitespace-nowrap hover:underline"
                    title="Add just this element to the canvas"
                    aria-label={`Add element ${element.id} to the canvas`}
                    onClick={() => onAddToCanvas(element)}
                  >
                    + canvas
                  </button>
                </td>
              )}
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}
