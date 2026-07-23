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
 * Query screen non-embedding flows (feature index-workspace): each family's exact
 * request payload behind the INDEX-FIRST interaction — pick the index from the live
 * inventory, the offered forms follow its capabilities — plus id-list hydration into
 * the result table, fulltext highlights, and the free-form fallback when no inventory
 * is known. The vector/semantic flows are pinned in embedding-query.test.tsx.
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
  indices: [
    { indexId: "nameIndex", pluginType: "DictionaryIndex", capabilities: ["equality"] },
    { indexId: "ageIndex", pluginType: "RangeIndex", capabilities: ["equality", "range"] },
    { indexId: "scoreIndex", pluginType: "RangeIndex", capabilities: ["equality", "range"] },
    { indexId: "ft", pluginType: "RegExIndex", capabilities: ["equality", "fulltext"] },
    { indexId: "geo", pluginType: "SpatialIndex", capabilities: ["spatial"] },
  ],
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

/** Switch to index mode and pick an inventory index (waits for the /status dropdown). */
async function pickIndex(user: ReturnType<typeof userEvent.setup>, indexId: string) {
  await user.selectOptions(screen.getByTestId("query-mode"), "index");
  await waitFor(() => expect(screen.getByTestId("index-select")).toBeInTheDocument());
  await user.selectOptions(screen.getByTestId("index-select"), indexId);
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

describe("index equality query", () => {
  it("sends indexId + operator + literal + result type", async () => {
    const user = userEvent.setup();
    renderScreen();

    await pickIndex(user, "nameIndex");
    // A single-capability index needs no form toggle — the one form is stated.
    expect(screen.getByTestId("form-single")).toHaveTextContent("equality / operator");
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

  it("blocks the run until an index is picked", async () => {
    const user = userEvent.setup();
    renderScreen();

    await user.selectOptions(screen.getByTestId("query-mode"), "index");
    await waitFor(() => expect(screen.getByTestId("index-select")).toBeInTheDocument());
    expect(screen.getByTestId("scan-run")).toBeDisabled();
  });
});

describe("range query", () => {
  it("sends both typed limits with their inclusivity flags", async () => {
    const user = userEvent.setup();
    renderScreen();

    await pickIndex(user, "ageIndex");
    // RangeIndex answers equality + range: the form toggle appears; pick range.
    await user.click(screen.getByTestId("form-range"));
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
    renderScreen();

    await pickIndex(user, "scoreIndex");
    await user.click(screen.getByTestId("form-range"));
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

describe("fulltext query", () => {
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
    renderScreen();

    await pickIndex(user, "ft");
    await user.click(screen.getByTestId("form-fulltext"));
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

  it("clears the previous highlight block when the next query is not fulltext", async () => {
    const user = userEvent.setup();
    scanFulltextMock.mockResolvedValue({
      maximumScore: 1,
      elements: [{ graphElementId: 7, score: 1, highlights: ["hit"] }],
    });
    getGraphElementMock.mockResolvedValue(vertex(7, "doc7"));
    renderScreen();

    await pickIndex(user, "ft");
    await user.click(screen.getByTestId("form-fulltext"));
    await user.type(screen.getByLabelText("query"), "hit");
    await user.click(screen.getByTestId("scan-run"));
    await waitFor(() => expect(screen.getByText(/highlights/)).toBeInTheDocument());

    scanPropertyMock.mockResolvedValue([]);
    await user.selectOptions(screen.getByTestId("query-mode"), "property");
    await user.type(screen.getByTestId("scan-property"), "age");
    await user.type(screen.getByTestId("scan-literal-value"), "1");
    await user.click(screen.getByTestId("scan-run"));

    await waitFor(() => expect(screen.getByText(/results — 0 ids/)).toBeInTheDocument());
    expect(screen.queryByText(/highlights/)).not.toBeInTheDocument();
  });
});

describe("spatial query", () => {
  it("sends numeric graphElementId and distance", async () => {
    const user = userEvent.setup();
    scanSpatialMock.mockResolvedValue([]);
    renderScreen();

    await pickIndex(user, "geo");
    // Spatial is the index's only capability — its form is active without a toggle.
    expect(screen.getByTestId("form-single")).toHaveTextContent("spatial");
    // A blank element id must not run: Number("") is 0, which would silently query the
    // neighborhood of element 0.
    expect(screen.getByTestId("scan-run")).toBeDisabled();
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

describe("free-form fallback", () => {
  it("offers a free index input and every query form when /status predates the inventory", async () => {
    const user = userEvent.setup();
    getStatusMock.mockResolvedValue({ ...STATUS, indices: null });
    renderScreen();

    await user.selectOptions(screen.getByTestId("query-mode"), "index");
    const free = await screen.findByTestId("index-free");
    await user.type(free, "adhoc");

    // Unknown index: all five forms stay available; pick fulltext and query.
    await user.click(screen.getByTestId("form-fulltext"));
    await user.type(screen.getByLabelText("query"), "needle");
    await user.click(screen.getByTestId("scan-run"));

    await waitFor(() => expect(scanFulltextMock).toHaveBeenCalledTimes(1));
    expect(scanFulltextMock.mock.calls[0][1]).toEqual({
      indexId: "adhoc",
      requestString: "needle",
    });

    // An unknown index answering 0 ids is ambiguous (missing index / wrong form) — the
    // non-vector endpoints answer empty rather than erroring, so the panel says so.
    await waitFor(() =>
      expect(screen.getByTestId("unknown-index-hint")).toHaveTextContent("'adhoc'"),
    );
  });

  it("a known-but-empty inventory keeps the dropdown and points at the Indexes screen", async () => {
    const user = userEvent.setup();
    getStatusMock.mockResolvedValue({ ...STATUS, indices: [] });
    renderScreen();

    await user.selectOptions(screen.getByTestId("query-mode"), "index");
    await waitFor(() => expect(screen.getByTestId("no-indexes-note")).toBeInTheDocument());
    expect(screen.getByTestId("index-select")).toBeInTheDocument();
    expect(screen.queryByTestId("index-free")).not.toBeInTheDocument();
    expect(screen.getByTestId("scan-run")).toBeDisabled();
  });
});

describe("query errors", () => {
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
