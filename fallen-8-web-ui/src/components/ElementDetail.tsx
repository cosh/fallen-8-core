import { useActiveInstance } from "../instances/registry";
import type { EdgeREST, VertexREST } from "../api/types";
import { isEdge } from "../lib/hydrate";
import { EmbeddingsTab } from "./EmbeddingsTab";
import { InspectLink } from "./InspectLink";
import { PropertiesTab } from "./PropertiesTab";

export function ElementDetail({
  element,
  providerEnabled,
  onRefresh,
  tab,
  onTabChange,
}: {
  element: VertexREST | EdgeREST;
  providerEnabled: boolean | null;
  onRefresh: () => void;
  /** Owned by the screen: a refresh unmounts this panel while the lookup is pending, so
   *  local state would snap back to "properties" right after every embedding write. */
  tab: "properties" | "embeddings";
  onTabChange: (tab: "properties" | "embeddings") => void;
}) {
  const instance = useActiveInstance()!;
  const edge = isEdge(element) ? element : null;

  return (
    <div className="panel">
      <div className="panel-title">
        {edge ? "edge" : "vertex"} #{element.id}
      </div>
      <div className="space-y-2 p-3 text-[12px]">
        <div>
          <span className="text-fg-faint">label </span>
          {element.label ?? "—"}
        </div>
        <div className="text-fg-dim">
          created {element.creationDate} · modified {element.modificationDate}
        </div>
        {edge && (
          <div>
            <span className="text-fg-faint">endpoints </span>
            <InspectLink id={edge.sourceVertex} /> → <InspectLink id={edge.targetVertex} />
          </div>
        )}
        <div className="border-line flex gap-1 border-b">
          {(["properties", "embeddings"] as const).map((t) => (
            <button
              key={t}
              type="button"
              data-testid={`element-tab-${t}`}
              className={`px-2 py-1 text-[11px] tracking-wider uppercase ${
                tab === t
                  ? "border-accent text-accent border-b-2"
                  : "text-fg-dim hover:text-fg"
              }`}
              onClick={() => onTabChange(t)}
            >
              {t}
            </button>
          ))}
        </div>
        {tab === "properties" ? (
          <PropertiesTab properties={element.properties ?? []} />
        ) : (
          <EmbeddingsTab
            instance={instance}
            element={element}
            providerEnabled={providerEnabled}
            onRefresh={onRefresh}
          />
        )}
      </div>
    </div>
  );
}
