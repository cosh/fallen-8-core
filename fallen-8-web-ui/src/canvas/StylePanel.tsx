// MIT License
//
// StylePanel.tsx
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

import { Field } from "../components/Field";
import { help } from "../lib/fieldHelp";
import type { StyleConfig } from "./styleConfig";

/**
 * Sectioned canvas style configuration (studio-canvas-viz FR-8). Pure controls: reads
 * the config, emits patches; persistence and resolution live elsewhere. Property
 * pickers suggest keys seen on the canvas but accept free text (properties may arrive
 * with later merges); switching a control to "property" seeds the field with the first
 * such key (see defaultProperty) so it is never blank, and the user edits from there.
 * The property field stacks on its OWN full-width line under the picker (space-y-1): the
 * shared `.input` class is unlayered so it wins over a `w-*` utility, so a side-by-side
 * narrow select would keep 100% width and clip the field out of the 320px panel.
 */

function PropertyInput({
  id,
  value,
  listId,
  onChange,
}: {
  id: string;
  value: string;
  listId: string;
  onChange: (value: string) => void;
}) {
  return (
    <input
      id={id}
      className="input"
      list={listId}
      placeholder="property id"
      value={value}
      onChange={(e) => onChange(e.target.value)}
    />
  );
}

/**
 * Seed value for a control that has just switched to "property": keep what the user
 * already typed, otherwise default to the first property key seen on the canvas so the
 * field is never blank (empty only when the canvas has no properties yet). The user then
 * customizes it freely — the input stays free text.
 */
function defaultProperty(current: string, suggestedKeys: string[]): string {
  return current || suggestedKeys[0] || "";
}

function Section({ title, children }: { title: string; children: React.ReactNode }) {
  return (
    <details open className="border-line border-b last:border-b-0">
      <summary className="text-fg-dim cursor-pointer px-3 py-1.5 text-[11px] font-semibold select-none">
        {title}
      </summary>
      <div className="space-y-2 px-3 pb-3">{children}</div>
    </details>
  );
}

export function StylePanel({
  config,
  onChange,
  nodePropertyKeys,
  edgePropertyKeys,
}: {
  config: StyleConfig;
  onChange: (patch: Partial<StyleConfig>) => void;
  nodePropertyKeys: string[];
  edgePropertyKeys: string[];
}) {
  return (
    <div data-testid="style-panel" className="text-[12px]">
      <datalist id="canvas-node-props">
        {nodePropertyKeys.map((key) => (
          <option key={key} value={key} />
        ))}
      </datalist>
      <datalist id="canvas-edge-props">
        {edgePropertyKeys.map((key) => (
          <option key={key} value={key} />
        ))}
      </datalist>

      <Section title="renderer & layout">
        <Field helpKey="canvasRenderer" label="renderer" htmlFor="style-renderer">
          <select
            id="style-renderer"
            data-testid="style-renderer"
            className="input"
            value={config.renderer}
            onChange={(e) => onChange({ renderer: e.target.value as StyleConfig["renderer"] })}
          >
            <option value="2d">2D (Sigma, WebGL)</option>
            <option value="3d">3D (three.js, WebGL)</option>
          </select>
        </Field>
        <Field helpKey="canvasLayout" label="layout" htmlFor="style-layout">
          {config.renderer === "2d" ? (
            <select
              id="style-layout"
              data-testid="style-layout"
              className="input"
              value={config.layout2d}
              onChange={(e) => onChange({ layout2d: e.target.value as StyleConfig["layout2d"] })}
            >
              <option value="force">force (FA2)</option>
              <option value="circular">circular</option>
              <option value="circlepack">circle pack (by label)</option>
              <option value="grid">grid</option>
              <option value="random">random</option>
            </select>
          ) : (
            <select
              id="style-layout"
              data-testid="style-layout"
              className="input"
              value={config.layout3d}
              onChange={(e) => onChange({ layout3d: e.target.value as StyleConfig["layout3d"] })}
            >
              <option value="force">force (d3-force-3d)</option>
              <option value="dag-td">dag · top-down</option>
              <option value="dag-radial">dag · radial</option>
            </select>
          )}
        </Field>
      </Section>

      <Section title="nodes">
        <Field helpKey="canvasNodeColor" label="color by" htmlFor="style-node-color-mode">
          <div className="space-y-1">
            <select
              id="style-node-color-mode"
              className="input"
              value={config.nodeColorMode}
              onChange={(e) => {
                const mode = e.target.value as StyleConfig["nodeColorMode"];
                onChange({
                  nodeColorMode: mode,
                  ...(mode === "property"
                    ? { nodeColorProperty: defaultProperty(config.nodeColorProperty, nodePropertyKeys) }
                    : {}),
                });
              }}
            >
              <option value="label">label</option>
              <option value="property">property</option>
            </select>
            {config.nodeColorMode === "property" && (
              <PropertyInput
                id="style-node-color-prop"
                value={config.nodeColorProperty}
                listId="canvas-node-props"
                onChange={(v) => onChange({ nodeColorProperty: v })}
              />
            )}
          </div>
        </Field>
        <Field helpKey="canvasNodeSize" label="size by" htmlFor="style-node-size-mode">
          <div className="space-y-1">
            <select
              id="style-node-size-mode"
              className="input"
              value={config.nodeSizeMode}
              onChange={(e) => {
                const mode = e.target.value as StyleConfig["nodeSizeMode"];
                onChange({
                  nodeSizeMode: mode,
                  ...(mode === "property"
                    ? { nodeSizeProperty: defaultProperty(config.nodeSizeProperty, nodePropertyKeys) }
                    : {}),
                });
              }}
            >
              <option value="fixed">fixed</option>
              <option value="property">property</option>
              <option value="in-degree">in-degree</option>
              <option value="out-degree">out-degree</option>
              <option value="degree">degree</option>
            </select>
            {config.nodeSizeMode === "property" && (
              <PropertyInput
                id="style-node-size-prop"
                value={config.nodeSizeProperty}
                listId="canvas-node-props"
                onChange={(v) => onChange({ nodeSizeProperty: v })}
              />
            )}
          </div>
        </Field>
        <Field helpKey="canvasNodeImage" label="image / emoji property" htmlFor="style-node-image-prop">
          <PropertyInput
            id="style-node-image-prop"
            value={config.nodeImageProperty}
            listId="canvas-node-props"
            onChange={(v) => onChange({ nodeImageProperty: v })}
          />
        </Field>
      </Section>

      <Section title="edges">
        <Field helpKey="canvasEdgeColor" label="color by" htmlFor="style-edge-color-mode">
          <div className="space-y-1">
            <select
              id="style-edge-color-mode"
              className="input"
              value={config.edgeColorMode}
              onChange={(e) => {
                const mode = e.target.value as StyleConfig["edgeColorMode"];
                onChange({
                  edgeColorMode: mode,
                  ...(mode === "property"
                    ? { edgeColorProperty: defaultProperty(config.edgeColorProperty, edgePropertyKeys) }
                    : {}),
                });
              }}
            >
              <option value="label">label</option>
              <option value="property">property</option>
            </select>
            {config.edgeColorMode === "property" && (
              <PropertyInput
                id="style-edge-color-prop"
                value={config.edgeColorProperty}
                listId="canvas-edge-props"
                onChange={(v) => onChange({ edgeColorProperty: v })}
              />
            )}
          </div>
        </Field>
        <Field helpKey="canvasEdgeWidth" label="width by" htmlFor="style-edge-width-mode">
          <div className="space-y-1">
            <select
              id="style-edge-width-mode"
              className="input"
              value={config.edgeWidthMode}
              onChange={(e) => {
                const mode = e.target.value as StyleConfig["edgeWidthMode"];
                onChange({
                  edgeWidthMode: mode,
                  ...(mode === "property"
                    ? { edgeWidthProperty: defaultProperty(config.edgeWidthProperty, edgePropertyKeys) }
                    : {}),
                });
              }}
            >
              <option value="fixed">fixed</option>
              <option value="property">property</option>
            </select>
            {config.edgeWidthMode === "property" && (
              <PropertyInput
                id="style-edge-width-prop"
                value={config.edgeWidthProperty}
                listId="canvas-edge-props"
                onChange={(v) => onChange({ edgeWidthProperty: v })}
              />
            )}
          </div>
        </Field>
      </Section>

      <Section title="labels & effects">
        <label className="flex items-center gap-2" title={help("canvasNodeLabels")}>
          <input
            type="checkbox"
            checked={config.showNodeLabels}
            onChange={(e) => onChange({ showNodeLabels: e.target.checked })}
          />
          node labels
        </label>
        <label className="flex items-center gap-2" title={help("canvasEdgeLabels")}>
          <input
            type="checkbox"
            checked={config.showEdgeLabels}
            onChange={(e) => onChange({ showEdgeLabels: e.target.checked })}
          />
          edge labels
        </label>
        <label className="flex items-center gap-2" title={help("canvasEdgeArrows")}>
          <input
            type="checkbox"
            checked={config.edgeArrows}
            onChange={(e) => onChange({ edgeArrows: e.target.checked })}
          />
          directed arrowheads
        </label>
      </Section>
    </div>
  );
}
