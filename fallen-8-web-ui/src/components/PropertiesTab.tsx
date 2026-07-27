// MIT License
//
// PropertiesTab.tsx
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
import type { PropertyREST } from "../api/types";
import { help } from "../lib/fieldHelp";
import { isReservedEmbeddingProperty, previewVector } from "../lib/embeddingProperties";
import { DISPLAY_CAP } from "../lib/truncate";
import { SCROLL_ROWS, capList, scrollRows } from "../lib/listCaps";
import { ListCapNote } from "./ListCapNote";
import { Truncated } from "./Truncated";

export function PropertiesTab({ properties }: { properties: PropertyREST[] }) {
  const [showReserved, setShowReserved] = useState(false);
  const visible = showReserved
    ? properties
    : properties.filter((p) => !isReservedEmbeddingProperty(p.propertyId));
  const hasReserved = properties.some((p) => isReservedEmbeddingProperty(p.propertyId));
  const shownProps = capList(visible);

  return (
    <div className="space-y-2" data-testid="properties-tab">
      <div className="scroll-list" style={scrollRows(SCROLL_ROWS.default)}>
      <table className="w-full">
        <thead>
          <tr className="text-fg-faint">
            <th className="table-cell">property</th>
            <th className="table-cell">value</th>
            <th className="table-cell">type</th>
          </tr>
        </thead>
        <tbody>
          {shownProps.shown.map((p) => (
            <tr key={p.propertyId}>
              <td className="table-cell">
                <Truncated text={p.propertyId} max={DISPLAY_CAP.propertyKey} />
              </td>
              <td className="table-cell">
                {/* previewVector caps a vector value (even under a non-reserved key) to a
                    short preview; Truncated then caps any other long text, full value in title. */}
                <Truncated text={previewVector(p.propertyValue)} max={DISPLAY_CAP.propertyValue} />
              </td>
              <td className="table-cell text-fg-dim">
                <Truncated text={p.fullQualifiedTypeName ?? "—"} max={DISPLAY_CAP.typeName} />
              </td>
            </tr>
          ))}
          {shownProps.total === 0 && (
            <tr>
              <td className="table-cell text-fg-faint" colSpan={3}>
                no properties
              </td>
            </tr>
          )}
        </tbody>
      </table>
      </div>
      <ListCapNote shown={shownProps.shown.length} total={shownProps.total} />
      {hasReserved && (
        <label
          className="text-fg-dim label-help flex items-center gap-1 text-[11px]"
          title={help("embeddingShowReserved")}
        >
          <input
            type="checkbox"
            data-testid="show-reserved"
            checked={showReserved}
            onChange={(event) => setShowReserved(event.target.checked)}
          />
          show reserved embedding properties (folded into the Embeddings tab)
        </label>
      )}
    </div>
  );
}
