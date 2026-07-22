import { useState } from "react";
import type { PropertyREST } from "../api/types";
import { formatPropertyValue } from "../lib/literals";
import { help } from "../lib/fieldHelp";
import { isReservedEmbeddingProperty, previewVector } from "../lib/embeddingProperties";

export function PropertiesTab({ properties }: { properties: PropertyREST[] }) {
  const [showReserved, setShowReserved] = useState(false);
  const visible = showReserved
    ? properties
    : properties.filter((p) => !isReservedEmbeddingProperty(p.propertyId));
  const hasReserved = properties.some((p) => isReservedEmbeddingProperty(p.propertyId));

  return (
    <div className="space-y-2" data-testid="properties-tab">
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
              <td className="table-cell">{p.propertyId}</td>
              <td className="table-cell">
                {isReservedEmbeddingProperty(p.propertyId)
                  ? previewVector(p.propertyValue)
                  : formatPropertyValue(p.propertyValue)}
              </td>
              <td className="table-cell text-fg-dim">{p.fullQualifiedTypeName ?? "—"}</td>
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
