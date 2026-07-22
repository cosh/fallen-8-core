import { beforeEach, describe, expect, it, vi } from "vitest";
import { render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import type { InstanceConfig } from "../src/instances/types";
import type {
  EdgeREST,
  FulltextIndexScanSpecification,
  FulltextSearchResultREST,
  IndexScanSpecification,
  RangeIndexScanSpecification,
  ScanSpecification,
  SearchDistanceSpecification,
  StatusREST,
  VertexREST,
} from "../src/api/types";

/**
 * Query screen non-embedding scan flows (feature structural-decomposition, Phase 3
 * pinning): each scan family's exact request payload, id-list hydration into the result
 * table, and the fulltext highlight rendering — pinned BEFORE the screen is decomposed.
 * The vector/semantic flows are pinned in embedding-query.test.tsx.
 */

const getStatusMock =
  vi.fn<(i: InstanceConfig, signal?: AbortSignal) => Promise<StatusREST | null>>();
const scanPropertyMock =
  vi.fn<(i: InstanceConfig, propertyId: string, spec: ScanSpecification) => Promise<number[] | null>>();
const scanIndexMock =
  vi.fn<(i: InstanceConfig, spec: IndexScanSpecification) => Promise<number[] | null>>();
const scanIndexRangeMock =
  vi.fn<(i: InstanceConfig, spec: RangeIndexScanSpecification) => Promise<number[] | null>>();
const scanFulltextMock =
  vi.fn<(i: InstanceConfig, spec: FulltextIndexScanSpecification) => Promise<FulltextSearchResultREST | null>>();
const scanSpatialMock =
  vi.fn<(i: InstanceConfig, spec: SearchDistanceSpecification) => Promise<number[] | null>>();
const getGraphElementMock =
  vi.fn<(i: InstanceConfig, id: number, signal?: AbortSignal) => Promise<VertexREST | EdgeREST | null>>();

vi.mock("../src/api/endpoints", async (importOriginal) => {
  const original = await importOriginal<typeof import("../src/api/endpoints")>();
  return {
    ...original,
    getStatus: (i: InstanceConfig, signal?: AbortSignal) => getStatusMock(i, signal),
    scanProperty: (i: InstanceConfig, propertyId: string, spec: ScanSpecification) =>
      scanPropertyMock(i, propertyId, spec),
    scanIndex: (i: InstanceConfig, spec: IndexScanSpecification) => scanIndexMock(i, spec),
    scanIndexRange: (i: InstanceConfig, spec: RangeIndexScanSpecification) =>
      scanIndexRangeMock(i, spec),
    scanFulltext: (i: InstanceConfig, spec: FulltextIndexScanSpecification) =>
      scanFulltextMock(i, spec),
    scanSpatial: (i: InstanceConfig, spec: SearchDistanceSpecification) =>
      scanSpatialMock(i, spec),
    getGraphElement: (i: InstanceConfig, id: number, signal?: AbortSignal) =>
      getGraphElementMock(i, id, signal),
  };
});

import { QueryScreen } from "../src/screens/QueryScreen";

const STATUS: StatusREST = {
  vertexCount: 0,
  edgeCount: 0,
  usedMemory: 0,
  indices: [],
  availableIndexPlugins: ["DictionaryIndex"],
  availablePathPlugins: [],
  availableAnalyticsPlugins: [],
  availableServicePlugins: [],
};

function vertex(id: number, label: string): VertexREST {
  return {
    id,
    creationDate: "2026-01-01",
    modificationDate: "2026-01-01",
    label,
    kind: "vertex",
    properties: [
      { propertyId: "age", propertyValue: 30 + id, fullQualifiedTypeName: "System.Int32" },
    ],
  };
}

function edge(id: number, source: number, target: number): EdgeREST {
  return {
    id,
    creationDate: "2026-01-01",
    modificationDate: "2026-01-01",
    label: "knows",
    kind: "edge",
    sourceVertex: source,
    targetVertex: target,
    properties: [],
  };
}

function renderScreen() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={client}>
      <QueryScreen />
    </QueryClientProvider>,
  );
}

beforeEach(() => {
  getStatusMock.mockReset().mockResolvedValue(STATUS);
  scanPropertyMock.mockReset().mockResolvedValue([]);
  scanIndexMock.mockReset().mockResolvedValue([]);
  scanIndexRangeMock.mockReset().mockResolvedValue([]);
  scanFulltextMock.mockReset().mockResolvedValue({ maximumScore: 0, elements: [] });
  scanSpatialMock.mockReset().mockResolvedValue([]);
  getGraphElementMock.mockReset().mockResolvedValue(null);
});

describe("property scan", () => {
  it("sends the typed literal + operator number and hydrates ids into the result table", async () => {
    const user = userEvent.setup();
    scanPropertyMock.mockResolvedValue([1, 2]);
    getGraphElementMock.mockImplementation(async (_i, id) =>
      id === 1 ? vertex(1, "alice") : edge(2, 1, 3),
    );
    renderScreen();

    await user.type(screen.getByTestId("scan-property"), "age");
    await user.selectOptions(screen.getByLabelText("operator"), "GreaterOrEquals");
    await user.selectOptions(screen.getByLabelText("literal type"), "System.Int32");
    await user.type(screen.getByTestId("scan-literal-value"), "30");
    await user.selectOptions(screen.getByLabelText("result type"), "Both");
    await user.click(screen.getByTestId("scan-run"));

    await waitFor(() => expect(scanPropertyMock).toHaveBeenCalledTimes(1));
    expect(scanPropertyMock.mock.calls[0][1]).toBe("age");
    // Operator NAMES map to the wire's operator NUMBERS (GreaterOrEquals = 2).
    expect(scanPropertyMock.mock.calls[0][2]).toEqual({
      operator: 2,
      literal: { value: "30", fullQualifiedTypeName: "System.Int32" },
      resultType: "Both",
    });

    await waitFor(() => expect(screen.getByText(/results — 2 ids/)).toBeInTheDocument());
    // Hydrated rows: a vertex renders its label, an edge its endpoints.
    expect(screen.getByText("alice")).toBeInTheDocument();
    expect(screen.getByText("1 → 3")).toBeInTheDocument();
  });

  it("carries a non-default result type into the payload", async () => {
    const user = userEvent.setup();
    renderScreen();

    await user.type(screen.getByTestId("scan-property"), "age");
    await user.type(screen.getByTestId("scan-literal-value"), "1");
    await user.selectOptions(screen.getByLabelText("result type"), "Edges");
    await user.click(screen.getByTestId("scan-run"));

    await waitFor(() => expect(scanPropertyMock).toHaveBeenCalledTimes(1));
    expect(scanPropertyMock.mock.calls[0][2].resultType).toBe("Edges");
  });

  it("skips ids whose hydration fails (deleted between scan and hydration)", async () => {
    const user = userEvent.setup();
    scanPropertyMock.mockResolvedValue([1, 2, 3]);
    getGraphElementMock.mockImplementation(async (_i, id) => {
      if (id === 2) throw new Error("gone");
      return vertex(id, `v${id}`);
    });
    renderScreen();

    await user.type(screen.getByTestId("scan-property"), "age");
    await user.type(screen.getByTestId("scan-literal-value"), "x");
    await user.click(screen.getByTestId("scan-run"));

    // The id count reports the scan's answer; the table shows what still exists.
    await waitFor(() => expect(screen.getByText(/results — 3 ids/)).toBeInTheDocument());
    expect(screen.getByText("v1")).toBeInTheDocument();
    expect(screen.getByText("v3")).toBeInTheDocument();
    expect(screen.queryByText("v2")).not.toBeInTheDocument();
  });

  it("renders an empty result honestly without hydrating", async () => {
    const user = userEvent.setup();
    scanPropertyMock.mockResolvedValue([]);
    renderScreen();

    await user.type(screen.getByTestId("scan-property"), "age");
    await user.type(screen.getByTestId("scan-literal-value"), "0");
    await user.click(screen.getByTestId("scan-run"));

    await waitFor(() => expect(screen.getByText(/results — 0 ids/)).toBeInTheDocument());
    expect(screen.getByText("No elements.")).toBeInTheDocument();
    expect(getGraphElementMock).not.toHaveBeenCalled();
  });
});

describe("index scan", () => {
  it("sends indexId + operator + literal + result type", async () => {
    const user = userEvent.setup();
    const { container } = renderScreen();

    await user.selectOptions(screen.getByTestId("scan-kind"), "index");
    // Two "index id" fields exist (scan form + index management) — target by id.
    await user.type(container.querySelector("#scan-index")!, "nameIndex");
    await user.selectOptions(screen.getByLabelText("operator"), "NotEquals");
    await user.type(screen.getByTestId("scan-literal-value"), "Alice");
    await user.click(screen.getByTestId("scan-run"));

    await waitFor(() => expect(scanIndexMock).toHaveBeenCalledTimes(1));
    expect(scanIndexMock.mock.calls[0][1]).toEqual({
      indexId: "nameIndex",
      operator: 5,
      literal: { value: "Alice", fullQualifiedTypeName: "System.String" },
      resultType: "Both",
    });
  });
});

describe("range scan", () => {
  it("sends both typed limits with their inclusivity flags", async () => {
    const user = userEvent.setup();
    const { container } = renderScreen();

    await user.selectOptions(screen.getByTestId("scan-kind"), "range");
    await user.type(container.querySelector("#scan-index")!, "ageIndex");
    // Defaults are Int32 0..100, both ends inclusive; adjust the right limit + exclusivity.
    await user.clear(screen.getByTestId("range-right-value"));
    await user.type(screen.getByTestId("range-right-value"), "65");
    await user.click(screen.getByLabelText(/incl\. right/));
    await user.click(screen.getByTestId("scan-run"));

    await waitFor(() => expect(scanIndexRangeMock).toHaveBeenCalledTimes(1));
    expect(scanIndexRangeMock.mock.calls[0][1]).toEqual({
      indexId: "ageIndex",
      leftLimit: { value: "0", fullQualifiedTypeName: "System.Int32" },
      rightLimit: { value: "65", fullQualifiedTypeName: "System.Int32" },
      includeLeft: true,
      includeRight: false,
      resultType: "Both",
    });
  });

  it("carries a re-typed limit (Double) into the wire literal", async () => {
    const user = userEvent.setup();
    const { container } = renderScreen();

    await user.selectOptions(screen.getByTestId("scan-kind"), "range");
    await user.type(container.querySelector("#scan-index")!, "scoreIndex");
    await user.selectOptions(screen.getByLabelText("left limit type"), "System.Double");
    await user.clear(screen.getByTestId("range-left-value"));
    await user.type(screen.getByTestId("range-left-value"), "0.5");
    await user.click(screen.getByTestId("scan-run"));

    await waitFor(() => expect(scanIndexRangeMock).toHaveBeenCalledTimes(1));
    expect(scanIndexRangeMock.mock.calls[0][1].leftLimit).toEqual({
      value: "0.5",
      fullQualifiedTypeName: "System.Double",
    });
  });
});

describe("fulltext scan", () => {
  it("sends the request string and renders scores + highlights above the hydrated table", async () => {
    const user = userEvent.setup();
    scanFulltextMock.mockResolvedValue({
      maximumScore: 2.5,
      elements: [
        { graphElementId: 7, score: 2.5, highlights: ["a <b>red</b> bicycle", "second hit"] },
        { graphElementId: 9, score: 1.25, highlights: ["another <b>red</b> one"] },
      ],
    });
    getGraphElementMock.mockImplementation(async (_i, id) => vertex(id, `doc${id}`));
    const { container } = renderScreen();

    await user.selectOptions(screen.getByTestId("scan-kind"), "fulltext");
    await user.type(container.querySelector("#scan-index")!, "ft");
    await user.type(screen.getByLabelText("query"), "red bicycles");
    await user.click(screen.getByTestId("scan-run"));

    await waitFor(() => expect(scanFulltextMock).toHaveBeenCalledTimes(1));
    expect(scanFulltextMock.mock.calls[0][1]).toEqual({
      indexId: "ft",
      requestString: "red bicycles",
    });

    // Highlight block: max score header, then one line per match with its own score
    // and the highlights joined visibly.
    await waitFor(() =>
      expect(screen.getByText(/highlights \(max score 2\.50\)/)).toBeInTheDocument(),
    );
    expect(
      screen.getByText(/#7 \(2\.50\): a <b>red<\/b> bicycle … second hit/),
    ).toBeInTheDocument();
    expect(screen.getByText(/#9 \(1\.25\): another <b>red<\/b> one/)).toBeInTheDocument();

    // The matched elements are hydrated into the ordinary result table.
    await waitFor(() => expect(screen.getByText(/results — 2 ids/)).toBeInTheDocument());
    expect(screen.getByText("doc7")).toBeInTheDocument();
    expect(screen.getByText("doc9")).toBeInTheDocument();
  });

  it("clears the previous highlight block when the next scan is not fulltext", async () => {
    const user = userEvent.setup();
    scanFulltextMock.mockResolvedValue({
      maximumScore: 1,
      elements: [{ graphElementId: 7, score: 1, highlights: ["hit"] }],
    });
    getGraphElementMock.mockResolvedValue(vertex(7, "doc7"));
    const { container } = renderScreen();

    await user.selectOptions(screen.getByTestId("scan-kind"), "fulltext");
    await user.type(container.querySelector("#scan-index")!, "ft");
    await user.type(screen.getByLabelText("query"), "hit");
    await user.click(screen.getByTestId("scan-run"));
    await waitFor(() => expect(screen.getByText(/highlights/)).toBeInTheDocument());

    scanPropertyMock.mockResolvedValue([]);
    await user.selectOptions(screen.getByTestId("scan-kind"), "property");
    await user.type(screen.getByTestId("scan-property"), "age");
    await user.type(screen.getByTestId("scan-literal-value"), "1");
    await user.click(screen.getByTestId("scan-run"));

    await waitFor(() => expect(screen.getByText(/results — 0 ids/)).toBeInTheDocument());
    expect(screen.queryByText(/highlights/)).not.toBeInTheDocument();
  });
});

describe("spatial scan", () => {
  it("sends numeric graphElementId and distance", async () => {
    const user = userEvent.setup();
    scanSpatialMock.mockResolvedValue([]);
    const { container } = renderScreen();

    await user.selectOptions(screen.getByTestId("scan-kind"), "spatial");
    await user.type(container.querySelector("#scan-index")!, "geo");
    await user.type(screen.getByLabelText("element id"), "42");
    await user.clear(screen.getByLabelText("distance"));
    await user.type(screen.getByLabelText("distance"), "5.5");
    await user.click(screen.getByTestId("scan-run"));

    await waitFor(() => expect(scanSpatialMock).toHaveBeenCalledTimes(1));
    // Text inputs, numbers on the wire.
    expect(scanSpatialMock.mock.calls[0][1]).toEqual({
      indexId: "geo",
      graphElementId: 42,
      distance: 5.5,
    });
  });
});

describe("scan errors", () => {
  it("surfaces a failed scan in the error box without a results section", async () => {
    const user = userEvent.setup();
    scanPropertyMock.mockRejectedValue(new Error("boom"));
    renderScreen();

    await user.type(screen.getByTestId("scan-property"), "age");
    await user.type(screen.getByTestId("scan-literal-value"), "1");
    await user.click(screen.getByTestId("scan-run"));

    const alert = await screen.findByRole("alert");
    expect(within(alert).getByText("boom")).toBeInTheDocument();
    expect(screen.queryByText(/results —/)).not.toBeInTheDocument();
  });
});
