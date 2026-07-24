import { useState } from "react";
import type { PropertyREST } from "../api/types";
import { help } from "../lib/fieldHelp";
import { isReservedEmbeddingProperty, previewVector } from "../lib/embeddingProperties";
import { DISPLAY_CAP } from "../lib/truncate";
import { Truncated } from "./Truncated";

export function PropertiesTab({ properties }: { properties: PropertyREST[] }) {
  const [showReserved, setShowReserved] = useState(false);
  const visible = showReserved
    ? properties
    : properties.filter((p) => !isReservedEmbeddingProperty(p.propertyId));
  const hasReserved = properties.some((p) => isReservedEmbeddingProperty(p.propertyId));

  return (
    <div className="space-y-2 overflow-x-auto" data-testid="properties-tab">
      <table className="w-full">
        <thead>
          <tr className="text-fg-faint">
            <th className="table-cell">property</th>
            <th className="table-cell">value</th>
            <th className="table-cell">type</th>
          </tr>
        </thead>
        <tbody>
          {visible.map((p) => (
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
          {visible.length === 0 && (
            <tr>
              <td className="table-cell text-fg-faint" colSpan={3}>
                no properties
              </td>
            </tr>
          )}
        </tbody>
      </table>
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
