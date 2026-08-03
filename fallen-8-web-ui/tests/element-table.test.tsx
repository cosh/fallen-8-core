// MIT License
//
// element-table.test.tsx
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

import { describe, expect, it, vi } from "vitest";
import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { ElementTable } from "../src/components/ElementTable";
import type { EdgeREST, VertexREST } from "../src/api/types";

/**
 * The ElementTable's opt-in per-row "add to canvas" action (feature: per-row canvas add):
 * the action column and its buttons appear ONLY when onAddToCanvas is provided, and clicking a
 * row's button hands that exact element (vertex or edge) back to the caller.
 */

const vertex: VertexREST = {
  id: 1,
  creationDate: "2026-01-01",
  modificationDate: "2026-01-01",
  label: "alice",
  kind: "vertex",
  properties: [{ propertyId: "age", propertyValue: 30, fullQualifiedTypeName: "System.Int32" }],
};

const edge: EdgeREST = {
  id: 2,
  creationDate: "2026-01-01",
  modificationDate: "2026-01-01",
  label: "knows",
  kind: "edge",
  sourceVertex: 1,
  targetVertex: 3,
  properties: [],
};

describe("ElementTable per-row add-to-canvas", () => {
  it("renders no per-row canvas action when onAddToCanvas is absent (backward compatible)", () => {
    render(<ElementTable elements={[vertex, edge]} />);
    expect(screen.queryByTestId("row-to-canvas-1")).not.toBeInTheDocument();
    expect(screen.queryByTestId("row-to-canvas-2")).not.toBeInTheDocument();
  });

  it("renders one canvas button per row when onAddToCanvas is provided", () => {
    render(<ElementTable elements={[vertex, edge]} onAddToCanvas={() => {}} />);
    expect(screen.getByTestId("row-to-canvas-1")).toBeInTheDocument();
    expect(screen.getByTestId("row-to-canvas-2")).toBeInTheDocument();
  });

  it("hands the clicked row's element (vertex or edge) back to the caller", async () => {
    const user = userEvent.setup();
    const onAddToCanvas = vi.fn();
    render(<ElementTable elements={[vertex, edge]} onAddToCanvas={onAddToCanvas} />);

    await user.click(screen.getByTestId("row-to-canvas-1"));
    expect(onAddToCanvas).toHaveBeenLastCalledWith(vertex);

    await user.click(screen.getByTestId("row-to-canvas-2"));
    expect(onAddToCanvas).toHaveBeenLastCalledWith(edge);

    expect(onAddToCanvas).toHaveBeenCalledTimes(2);
  });

  it("still renders nothing extra for an empty element list", () => {
    render(<ElementTable elements={[]} onAddToCanvas={() => {}} />);
    expect(screen.getByText("No elements.")).toBeInTheDocument();
    expect(screen.queryByTestId(/row-to-canvas-/)).not.toBeInTheDocument();
  });
});
