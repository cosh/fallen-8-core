import type { EdgeREST, VertexREST } from "../api/types";
import { isEdge } from "../lib/hydrate";
import { formatPropertyValue } from "../lib/literals";

/** Hydrated element list as a table (FR-11 "open as table"). */
export function ElementTable({
  elements,
  onInspect,
}: {
  elements: (VertexREST | EdgeREST)[];
  onInspect?: (id: number) => void;
}) {
  if (elements.length === 0) {
    return <div className="text-fg-faint p-3 text-[12px]">No elements.</div>;
  }
  return (
    <div className="overflow-x-auto">
      <table className="w-full text-[12px]">
        <thead>
          <tr className="text-fg-faint">
            <th className="table-cell">id</th>
            <th className="table-cell">kind</th>
            <th className="table-cell">label</th>
            <th className="table-cell">endpoints</th>
            <th className="table-cell">properties</th>
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
              <td className="table-cell">{isEdge(element) ? "edge" : "vertex"}</td>
              <td className="table-cell">{element.label ?? "—"}</td>
              <td className="table-cell text-fg-dim">
                {isEdge(element)
                  ? `${element.sourceVertex} → ${element.targetVertex} (${element.edgePropertyId ?? "?"})`
                  : "—"}
              </td>
              <td className="table-cell text-fg-dim">
                {(element.properties ?? [])
                  .map((p) => `${p.propertyId}=${formatPropertyValue(p.propertyValue)}`)
                  .join(", ") || "—"}
              </td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}
