// MIT License
//
// ElementDetail.tsx
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

import { useInstanceStore } from "../instances/registry";
import type { EdgeREST, VertexREST } from "../api/types";
import { isEdge } from "../lib/hydrate";
import { EmbeddingsTab } from "./EmbeddingsTab";
import { InspectLink } from "./InspectLink";
import { PropertiesTab } from "./PropertiesTab";
import { Truncated } from "./Truncated";
import { DISPLAY_CAP } from "../lib/truncate";

export function ElementDetail({
  element,
  providerEnabled,
  onRefresh,
  onInspect,
  tab,
  onTabChange,
}: {
  element: VertexREST | EdgeREST;
  providerEnabled: boolean | null;
  onRefresh: () => void;
  onInspect: (id: number) => void;
  /** Owned by the screen so the chosen tab survives the unmount a missing-element
   *  lookup causes (hops and refreshes keep this panel mounted since adjacency-preview). */
  tab: "properties" | "embeddings";
  onTabChange: (tab: "properties" | "embeddings") => void;
}) {
  const { instance } = useInstanceStore();
  const edge = isEdge(element) ? element : null;

  return (
    <div className="panel">
      <div className="panel-title">
        {edge ? "edge" : "vertex"} #{element.id}
      </div>
      <div className="space-y-2 p-3 text-[12px]">
        <div className="flex gap-1">
          <span className="text-fg-faint shrink-0">label </span>
          <Truncated text={element.label ?? "—"} max={DISPLAY_CAP.label} />
        </div>
        <div className="text-fg-dim">
          created {element.creationDate} · modified {element.modificationDate}
        </div>
        {edge && (
          <div>
            <span className="text-fg-faint">endpoints </span>
            <InspectLink id={edge.sourceVertex} onInspect={onInspect} /> →{" "}
            <InspectLink id={edge.targetVertex} onInspect={onInspect} />
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
