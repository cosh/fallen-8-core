// MIT License
//
// style-panel.test.tsx
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
import { describe, expect, it, vi } from "vitest";
import { render } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { StylePanel } from "../src/canvas/StylePanel";
import { DEFAULT_STYLE_CONFIG, type StyleConfig } from "../src/canvas/styleConfig";

/**
 * Canvas style panel: switching a control to "property" seeds the property field with
 * the first key seen on the canvas (never blank, always editable), preserves a value the
 * user already chose, and leaves the stored property untouched for non-property modes.
 */

/** Stateful host so emitted patches feed back into the config, like the real screen. */
function Harness({
  initial,
  nodeKeys = [],
  edgeKeys = [],
  onPatch,
}: {
  initial?: Partial<StyleConfig>;
  nodeKeys?: string[];
  edgeKeys?: string[];
  onPatch?: (patch: Partial<StyleConfig>) => void;
}) {
  const [config, setConfig] = useState<StyleConfig>({ ...DEFAULT_STYLE_CONFIG, ...initial });
  return (
    <StylePanel
      config={config}
      onChange={(patch) => {
        onPatch?.(patch);
        setConfig((c) => ({ ...c, ...patch }));
      }}
      nodePropertyKeys={nodeKeys}
      edgePropertyKeys={edgeKeys}
    />
  );
}

const input = (id: string) => document.getElementById(id) as HTMLInputElement | null;

describe("StylePanel property seeding", () => {
  it.each([
    ["node color", "style-node-color-mode", "style-node-color-prop", "node"],
    ["node size", "style-node-size-mode", "style-node-size-prop", "node"],
    ["edge color", "style-edge-color-mode", "style-edge-color-prop", "edge"],
    ["edge width", "style-edge-width-mode", "style-edge-width-prop", "edge"],
  ])(
    "%s: switching to 'property' reveals the field seeded with the first canvas key",
    async (_label, modeId, propId, side) => {
      const user = userEvent.setup();
      render(
        <Harness
          nodeKeys={side === "node" ? ["age", "name"] : []}
          edgeKeys={side === "edge" ? ["weight", "since"] : []}
        />,
      );
      expect(input(propId)).toBeNull(); // hidden until property mode

      await user.selectOptions(document.getElementById(modeId)!, "property");

      const seeded = side === "node" ? "age" : "weight";
      expect(input(propId)).not.toBeNull();
      expect(input(propId)!.value).toBe(seeded);
    },
  );

  it("leaves the field blank (placeholder only) when the canvas has no properties", async () => {
    const user = userEvent.setup();
    render(<Harness nodeKeys={[]} />);

    await user.selectOptions(document.getElementById("style-node-color-mode")!, "property");

    const field = input("style-node-color-prop")!;
    expect(field.value).toBe("");
    expect(field.placeholder).toBe("property id");
  });

  it("does not overwrite a property the user already customized when re-entering property mode", async () => {
    const user = userEvent.setup();
    render(<Harness initial={{ nodeColorProperty: "department" }} nodeKeys={["age", "name"]} />);

    // label -> property, then away to label, then back: the custom value survives.
    await user.selectOptions(document.getElementById("style-node-color-mode")!, "property");
    expect(input("style-node-color-prop")!.value).toBe("department");

    await user.selectOptions(document.getElementById("style-node-color-mode")!, "label");
    expect(input("style-node-color-prop")).toBeNull();

    await user.selectOptions(document.getElementById("style-node-color-mode")!, "property");
    expect(input("style-node-color-prop")!.value).toBe("department");
  });

  it("keeps the field free text: the user can replace the seeded key", async () => {
    const user = userEvent.setup();
    render(<Harness nodeKeys={["age", "name"]} />);

    await user.selectOptions(document.getElementById("style-node-color-mode")!, "property");
    const field = input("style-node-color-prop")!;
    expect(field.value).toBe("age");

    await user.clear(field);
    await user.type(field, "score");
    expect(field.value).toBe("score");
  });

  it("suggests every canvas key via the shared datalist (node vs edge)", async () => {
    const user = userEvent.setup();
    render(<Harness nodeKeys={["age", "name"]} edgeKeys={["weight"]} />);

    await user.selectOptions(document.getElementById("style-node-color-mode")!, "property");
    expect(input("style-node-color-prop")!.getAttribute("list")).toBe("canvas-node-props");
    await user.selectOptions(document.getElementById("style-edge-color-mode")!, "property");
    expect(input("style-edge-color-prop")!.getAttribute("list")).toBe("canvas-edge-props");

    expect(document.querySelectorAll("#canvas-node-props option")).toHaveLength(2);
    expect(document.querySelectorAll("#canvas-edge-props option")).toHaveLength(1);
  });

  it("a non-property size mode carries no property override and preserves the stored key", async () => {
    const user = userEvent.setup();
    const onPatch = vi.fn();
    render(<Harness initial={{ nodeSizeProperty: "weight" }} nodeKeys={["age"]} onPatch={onPatch} />);

    await user.selectOptions(document.getElementById("style-node-size-mode")!, "degree");

    expect(onPatch).toHaveBeenCalledWith({ nodeSizeMode: "degree" });
    const patch = onPatch.mock.calls.at(-1)![0];
    expect(patch).not.toHaveProperty("nodeSizeProperty");
  });
});
