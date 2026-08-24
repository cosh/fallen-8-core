// MIT License
//
// find-similar-flow.test.tsx
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

import { beforeEach, describe, expect, it, vi } from "vitest";
import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import type { InstanceConfig } from "../src/instances/types";
import type {
  AGraphElementREST,
  StatusREST,
  VectorIndexScanSpecification,
  VectorSearchResultREST,
} from "../src/api/types";
import { getInstanceStore, resetInstanceStoresForTests } from "../src/state/instanceStore";
import { SAME_ORIGIN_INSTANCE } from "../src/instances/registry";

/**
 * The "find similar" gesture arriving on the Query screen (feature element-similarity-search).
 *
 * The gesture is composed on the client, because no route takes an element id as a kNN query. Two
 * things about it can only be checked here rather than in the pure helper: that the prefill lands as
 * a VECTOR-source query with the label inherited, and that the source element is asked for and then
 * dropped - k+1 requested, itself removed, k returned. Without the drop the answer to "what is like
 * this signal" is always that signal, at rank 1.
 */

const getStatusMock = vi.fn<(i: InstanceConfig) => Promise<StatusREST | null>>();
const scanVectorMock =
  vi.fn<
    (i: InstanceConfig, spec: VectorIndexScanSpecification) => Promise<VectorSearchResultREST | null>
  >();
const getGraphElementMock =
  vi.fn<(i: InstanceConfig, id: number, signal?: AbortSignal) => Promise<AGraphElementREST | null>>();

vi.mock("../src/api/endpoints", async (importOriginal) => {
  const original = await importOriginal<typeof import("../src/api/endpoints")>();
  return {
    ...original,
    getStatus: (i: InstanceConfig) => getStatusMock(i),
    scanVector: (i: InstanceConfig, spec: VectorIndexScanSpecification) => scanVectorMock(i, spec),
    getGraphElement: (i: InstanceConfig, id: number, s?: AbortSignal) =>
      getGraphElementMock(i, id, s),
  };
});

import { QueryScreen } from "../src/screens/QueryScreen";

const STATUS: StatusREST = {
  vertexCount: 0,
  edgeCount: 0,
  usedMemory: 0,
  indices: [
    {
      indexId: "arxml-summary",
      pluginType: "VectorIndex",
      embeddingName: "default",
      capabilities: ["vector"],
      keys: 3,
      values: 3,
    },
  ],
  availableIndexPlugins: ["VectorIndex"],
  availablePathPlugins: [],
  availableAnalyticsPlugins: [],
  availableServicePlugins: [],
};

const store = () => getInstanceStore(SAME_ORIGIN_INSTANCE.id);

function renderScreen() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={client}>
      <QueryScreen />
    </QueryClientProvider>,
  );
}

/** The gesture, as EmbeddingsTab / the canvas Detail panel issue it. */
function arriveFromElement8724() {
  store().getState().setScanPrefill({
    indexId: "arxml-summary",
    vectorText: "[0.1, 0.2, 0.3]",
    sourceElementId: 8724,
    label: "signal",
    kind: "vertex",
  });
}

beforeEach(() => {
  resetInstanceStoresForTests();
  localStorage.clear();
  getStatusMock.mockReset().mockResolvedValue(STATUS);
  getGraphElementMock.mockReset().mockResolvedValue(null);
  scanVectorMock.mockReset().mockResolvedValue({
    metric: "Cosine",
    higherIsBetter: true,
    results: [
      { graphElementId: 8724, score: 1 },
      { graphElementId: 4001, score: 0.93 },
    ],
  });
});

describe("a find-similar prefill lands as a vector query with the source's label", () => {
  it("selects the vector source, fills the vector, and inherits the label", async () => {
    arriveFromElement8724();
    renderScreen();

    await waitFor(() =>
      expect(screen.getByTestId("vector-query")).toHaveValue("[0.1, 0.2, 0.3]"),
    );
    expect(screen.getByLabelText(/label constraint/i)).toHaveValue("signal");
    // Consumed once: a prefill that survived would re-apply over a later edit.
    expect(store().getState().scanPrefill).toBeNull();
  });

  it("says which element it is excluding, rather than filtering silently", async () => {
    arriveFromElement8724();
    renderScreen();

    expect(await screen.findByTestId("exclude-source-chip")).toHaveTextContent("#8724");
  });
});

describe("the source element is asked for and then dropped", () => {
  it("requests k+1 and removes the source, so k real neighbours survive", async () => {
    arriveFromElement8724();
    renderScreen();
    await waitFor(() => expect(screen.getByTestId("vector-query")).toHaveValue("[0.1, 0.2, 0.3]"));

    await userEvent.click(screen.getByTestId("scan-run"));
    await waitFor(() => expect(scanVectorMock).toHaveBeenCalledTimes(1));

    // k+1, because the source element is its own nearest neighbour and would otherwise consume a slot.
    expect(scanVectorMock.mock.calls[0][1].k).toBe(11);
    expect(scanVectorMock.mock.calls[0][1].label).toBe("signal");
    expect(scanVectorMock.mock.calls[0][1].query).toEqual([0.1, 0.2, 0.3]);

    // One hit rendered, not two: #8724 was the rank-1 hit and is gone.
    await waitFor(() => expect(screen.getByText(/results — 1 ids/)).toBeInTheDocument());
  });

  it("asks for plain k once the exclusion is cleared, and keeps the source", async () => {
    arriveFromElement8724();
    renderScreen();
    await waitFor(() => expect(screen.getByTestId("vector-query")).toHaveValue("[0.1, 0.2, 0.3]"));

    await userEvent.click(await screen.findByTestId("exclude-source-clear"));
    await userEvent.click(screen.getByTestId("scan-run"));
    await waitFor(() => expect(scanVectorMock).toHaveBeenCalledTimes(1));

    expect(scanVectorMock.mock.calls[0][1].k).toBe(10);
    await waitFor(() => expect(screen.getByText(/results — 2 ids/)).toBeInTheDocument());
  });

  it("clamps the over-fetch at the engine's own k ceiling instead of asking for 1025", async () => {
    arriveFromElement8724();
    renderScreen();
    await waitFor(() => expect(screen.getByTestId("vector-query")).toHaveValue("[0.1, 0.2, 0.3]"));

    const k = screen.getByLabelText(/k \(1/);
    await userEvent.clear(k);
    await userEvent.type(k, "1024");
    await userEvent.click(screen.getByTestId("scan-run"));
    await waitFor(() => expect(scanVectorMock).toHaveBeenCalledTimes(1));

    // k is capped at 1024 server-side, so an unclamped +1 turns a find-similar search at the
    // advertised maximum into a 400. Losing one hit to the source element is the better failure.
    expect(scanVectorMock.mock.calls[0][1].k).toBe(1024);
  });

  it("drops the exclusion when the form is cleared, so it cannot filter an unrelated query", async () => {
    arriveFromElement8724();
    renderScreen();
    expect(await screen.findByTestId("exclude-source-chip")).toBeInTheDocument();

    await userEvent.click(screen.getByTestId("query-clear"));

    expect(screen.queryByTestId("exclude-source-chip")).not.toBeInTheDocument();
  });

  it("leaves an ordinary vector query untouched, with no over-fetch and no filtering", async () => {
    // No prefill: somebody pasted a vector by hand. Over-fetching there would silently return k+1
    // hits, or drop a legitimate one.
    renderScreen();
    await userEvent.selectOptions(screen.getByTestId("query-mode"), "index");
    await waitFor(() => expect(screen.getByTestId("index-select")).toBeInTheDocument());
    await userEvent.selectOptions(screen.getByTestId("index-select"), "arxml-summary");
    // Pasted, not typed: user-event reads "[" as the start of a key descriptor.
    await userEvent.click(screen.getByTestId("vector-query"));
    await userEvent.paste("[0.1, 0.2, 0.3]");
    await userEvent.click(screen.getByTestId("scan-run"));

    await waitFor(() => expect(scanVectorMock).toHaveBeenCalledTimes(1));
    expect(scanVectorMock.mock.calls[0][1].k).toBe(10);
    expect(screen.queryByTestId("exclude-source-chip")).not.toBeInTheDocument();
    await waitFor(() => expect(screen.getByText(/results — 2 ids/)).toBeInTheDocument());
  });
});
