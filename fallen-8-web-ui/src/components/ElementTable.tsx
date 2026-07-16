import type { EdgeREST, VertexREST } from "../api/types";
import { isEdge } from "../lib/hydrate";
import { formatPropertyValue } from "../lib/literals";

/**
 * Hydrated element list as a table (FR-11 "open as table"). `scores` adds the optional
 * score column shared by the vector scan and analytics top-K (concept spec §9);
 * `scoreHeader` names the metric so an L2 distance is never misread as a similarity.
 */
export function ElementTable({
  elements,
  onInspect,
  scores,
  scoreHeader = "score",
}: {
  elements: (VertexREST | EdgeREST)[];
  onInspect?: (id: number) => void;
  scores?: Map<number, number>;
  scoreHeader?: string;
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
            {scores && <th className="table-cell">{scoreHeader}</th>}
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
              {scores && (
                <td className="table-cell text-fg font-mono">
                  {scores.has(element.id) ? scores.get(element.id)!.toFixed(4) : "—"}
                </td>
              )}
              <td className="table-cell">{isEdge(element) ? "edge" : "vertex"}</td>
              <td className="table-cell">{element.label ?? "—"}</td>
              <td className="table-cell text-fg-dim">
                {isEdge(element)
                  ? `${element.sourceVertex} → ${element.targetVertex}`
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
