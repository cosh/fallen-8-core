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

/**
 * The properties cell TRUNCATES (DISPLAY_CAP.propertyValue = 80 chars) and the REST egress emits
 * properties in no guaranteed order, so what survives the cut is what the reader gets. Engine
 * bookkeeping must not spend that budget before the operator's own data.
 */
describe("ElementTable properties preview ordering", () => {
  const withEmbedding: VertexREST = {
    id: 7,
    creationDate: "2026-01-01",
    modificationDate: "2026-01-01",
    label: "movie",
    kind: "vertex",
    // Deliberately the order that produced the bad docs frame: a user key, then the ~44-character
    // reserved marker, then the key a reader actually came for.
    properties: [
      { propertyId: "year", propertyValue: 2010, fullQualifiedTypeName: "System.Int32" },
      {
        propertyId: "$embeddingModel:default",
        propertyValue: "bge-m3#1024#Cosine",
        fullQualifiedTypeName: "System.String",
      },
      { propertyId: "title", propertyValue: "Inception", fullQualifiedTypeName: "System.String" },
    ],
  };

  it("puts the element's own properties before the embedding markers", () => {
    render(<ElementTable elements={[withEmbedding]} />);

    const cell = screen.getByText(/year=2010/);
    // Both user keys precede the reserved one, so neither is what the truncation eats first.
    expect(cell.textContent).toMatch(/^year=2010, title=Inception, \$embeddingModel:default=/);
  });

  it("keeps the full, unreordered value in the title tooltip", () => {
    render(<ElementTable elements={[withEmbedding]} />);

    // Truncated's contract: nothing is lost, only reordered and clipped.
    const cell = screen.getByText(/year=2010/);
    expect(cell.getAttribute("title") ?? cell.textContent).toContain("title=Inception");
  });

  it("leaves an element with no reserved properties exactly as it came", () => {
    render(<ElementTable elements={[vertex]} />);
    expect(screen.getByText("age=30")).toBeInTheDocument();
  });
});

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
