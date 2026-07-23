import { beforeEach, describe, expect, it, vi } from "vitest";
import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import type { InstanceConfig } from "../src/instances/types";
import type {
  StatusREST,
  StoredQueryDetailREST,
  StoredQuerySummaryREST,
  SubGraphSpecification,
  SubGraphSummary,
} from "../src/api/types";
import { getInstanceStore, resetInstanceStoresForTests } from "../src/state/instanceStore";

/**
 * Subgraph screen semantic slots (feature subgraph-semantic-thresholds): a semantic
 * threshold is a MODE of a vertex-filter slot (top level and per vertex pattern step);
 * the request-level semantic query exists exactly while some slot consumes it, so the
 * inert enabled-but-unused block of the old UI is unrepresentable. Stored mode has no
 * slots; save-as-stored is blocked while a threshold is active (templates have no query
 * to bind). Monaco is mocked (the delegate slots pull it in transitively).
 */

vi.mock("../src/delegate/monacoSetup", () => ({ setupMonaco: () => {}, monaco: {} }));
vi.mock("@monaco-editor/react", () => ({
  default: ({ value }: { value: string }) => <textarea data-testid="mock-editor" value={value} readOnly />,
}));

const createSubGraphMock =
  vi.fn<(i: InstanceConfig, spec: SubGraphSpecification, from?: string) => Promise<SubGraphSummary | null>>();
let subGraphList: SubGraphSummary[] = [];

vi.mock("../src/api/endpoints", async (importOriginal) => {
  const original = await importOriginal<typeof import("../src/api/endpoints")>();
  return {
    ...original,
    listSubGraphSummaries: async () => subGraphList,
    createSubGraph: (i: InstanceConfig, spec: SubGraphSpecification, from?: string) =>
      createSubGraphMock(i, spec, from),
    listStoredQueries: async () =>
      [
        { name: "tpl", kind: "SubGraph", description: null, createdAt: "", compileState: "Compiled" },
      ] as StoredQuerySummaryREST[],
    getStoredQuery: async () =>
      ({
        name: "tpl",
        kind: "SubGraph",
        description: null,
        createdAt: "",
        compileState: "Compiled",
        specificationJson: "{}",
        compileDiagnostics: null,
      }) as StoredQueryDetailREST,
  };
});

import { SubgraphScreen } from "../src/screens/SubgraphScreen";

// Provider state rides /status (feature embedding-out-of-box).
function status(enabled: boolean): StatusREST {
  return {
    vertexCount: 0,
    edgeCount: 0,
    usedMemory: 0,
    indices: [],
    availableIndexPlugins: [],
    availablePathPlugins: [],
    availableAnalyticsPlugins: [],
    availableServicePlugins: [],
    embedding: {
      enabled,
      backend: "Onnx",
      modelName: "m",
      modelVersion: "",
      dimension: 2,
      intendedMetric: "Cosine",
      loaded: true,
    },
  };
}

function renderScreen(providerEnabled = true) {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  client.setQueryData(["local", "status"], status(providerEnabled));
  return render(
    <QueryClientProvider client={client}>
      <SubgraphScreen />
    </QueryClientProvider>,
  );
}

beforeEach(() => {
  resetInstanceStoresForTests();
  localStorage.clear();
  subGraphList = [];
  createSubGraphMock.mockReset().mockResolvedValue({ name: "s", vertexCount: 0, edgeCount: 0 });
});

describe("subgraph semantic slots", () => {
  it("the semantic query section exists exactly while a slot consumes it", async () => {
    const user = userEvent.setup();
    renderScreen(true);
    expect(screen.queryByTestId("sg-semantic-query")).not.toBeInTheDocument();

    await user.selectOptions(screen.getByTestId("sg-vf-mode"), "semantic");
    expect(screen.getByTestId("sg-semantic-query")).toBeInTheDocument();

    await user.selectOptions(screen.getByTestId("sg-vf-mode"), "everything");
    expect(screen.queryByTestId("sg-semantic-query")).not.toBeInTheDocument();
  });

  it("top-level semantic mode sends the query with minScore and no fragment", async () => {
    const user = userEvent.setup();
    renderScreen(true);
    await user.type(screen.getByTestId("sg-name"), "close");
    await user.selectOptions(screen.getByTestId("sg-vf-mode"), "semantic");
    await user.type(screen.getByTestId("sg-sem-vector"), "1, 0");

    await user.click(screen.getByTestId("sg-create"));
    await waitFor(() => expect(createSubGraphMock).toHaveBeenCalledTimes(1));
    const spec = createSubGraphMock.mock.calls[0][1];
    expect(spec.semantic).toEqual({
      embeddingName: "default",
      metric: "Cosine",
      queryVector: [1, 0],
      minScore: 0.7,
    });
    expect(spec.vertexFilter).toBeUndefined();
  });

  it("a vertex pattern step in semantic mode sends its own threshold", async () => {
    const user = userEvent.setup();
    renderScreen(true);
    await user.type(screen.getByTestId("sg-name"), "steps");
    await user.click(screen.getByTestId("add-vertex-step"));
    await user.selectOptions(screen.getByTestId("sg-p0-vf-mode"), "semantic");
    await user.type(screen.getByTestId("sg-sem-vector"), "1, 0");
    const threshold = screen.getByTestId("sg-p0-vf-minscore");
    await user.clear(threshold);
    await user.type(threshold, "0.6");

    await user.click(screen.getByTestId("sg-create"));
    await waitFor(() => expect(createSubGraphMock).toHaveBeenCalledTimes(1));
    const spec = createSubGraphMock.mock.calls[0][1];
    expect(spec.patterns?.[0].semanticMinScore).toBe(0.6);
    expect(spec.patterns?.[0].vertexFilter).toBeUndefined();
    // The top-level slot stayed on "match everything": a query travels, but no pre-filter.
    expect(spec.semantic?.queryVector).toEqual([1, 0]);
    expect(spec.semantic?.minScore).toBeUndefined();
    expect(spec.vertexFilter).toBeUndefined();
  });

  it("an invalid semantic query disables create with an inline error, not a fake server 400", async () => {
    const user = userEvent.setup();
    renderScreen(true);
    await user.type(screen.getByTestId("sg-name"), "x");
    await user.selectOptions(screen.getByTestId("sg-vf-mode"), "semantic");
    // The vector is left empty: the query cannot build, so submit must be gated.
    expect(screen.getByTestId("sg-sem-error")).toBeInTheDocument();
    expect(screen.getByTestId("sg-create")).toBeDisabled();
    expect(createSubGraphMock).not.toHaveBeenCalled();
  });

  it("a stale top-level fragment never travels once the slot is in semantic mode", async () => {
    // The fragment was written in fragment mode and stays in the draft; the MODE decides
    // what travels (regressing to `vertexFilter || undefined` must fail here).
    getInstanceStore("local").getState().setSubgraphDraft({
      vertexFilter: "return (v) => true;",
      vertexFilterMode: "semantic",
    });
    const user = userEvent.setup();
    renderScreen(true);
    await user.type(screen.getByTestId("sg-name"), "close");
    await user.type(screen.getByTestId("sg-sem-vector"), "1, 0");

    await user.click(screen.getByTestId("sg-create"));
    await waitFor(() => expect(createSubGraphMock).toHaveBeenCalledTimes(1));
    const spec = createSubGraphMock.mock.calls[0][1];
    expect(spec.vertexFilter).toBeUndefined();
    expect(spec.semantic?.minScore).toBe(0.7);
  });

  it("the typed semantic query survives the last slot leaving semantic mode", async () => {
    const user = userEvent.setup();
    renderScreen(true);
    await user.selectOptions(screen.getByTestId("sg-vf-mode"), "semantic");
    await user.type(screen.getByTestId("sg-sem-vector"), "1, 0");

    await user.selectOptions(screen.getByTestId("sg-vf-mode"), "everything");
    expect(screen.queryByTestId("sg-semantic-query")).not.toBeInTheDocument();

    await user.selectOptions(screen.getByTestId("sg-vf-mode"), "semantic");
    expect(screen.getByTestId("sg-sem-vector")).toHaveValue("1, 0");
  });

  it("a non-finite threshold blocks submit with an inline error", async () => {
    const user = userEvent.setup();
    renderScreen(true);
    await user.type(screen.getByTestId("sg-name"), "bad");
    await user.selectOptions(screen.getByTestId("sg-vf-mode"), "semantic");
    await user.type(screen.getByTestId("sg-sem-vector"), "1, 0");
    await user.clear(screen.getByTestId("sg-vf-minscore"));

    expect(screen.getByTestId("sg-vf-minscore-error")).toBeInTheDocument();
    expect(screen.getByTestId("sg-create")).toBeDisabled();
  });

  it("save-as-stored is blocked with a reason while a semantic slot is active", async () => {
    const user = userEvent.setup();
    renderScreen(true);
    await user.selectOptions(screen.getByTestId("sg-vf-mode"), "semantic");

    const save = screen.getByTestId("save-as-stored-query");
    expect(save).toBeDisabled();
    expect(save).toHaveAttribute("title", expect.stringContaining("stored template"));
  });

  it("stored mode has no slots and a stale semantic slot never blocks a stored create", async () => {
    const user = userEvent.setup();
    renderScreen(true);
    await user.type(screen.getByTestId("sg-name"), "fromTpl");

    // Put the top-level slot in semantic mode and leave the query INVALID (empty vector)...
    await user.selectOptions(screen.getByTestId("sg-vf-mode"), "semantic");

    // ...then switch to a stored template: no slots, no semantic query section.
    await user.click(screen.getByTestId("filter-source-stored"));
    expect(screen.queryByTestId("sg-semantic-query")).not.toBeInTheDocument();
    await user.selectOptions(screen.getByTestId("stored-query-select"), "tpl");

    await user.click(screen.getByTestId("sg-create"));
    await waitFor(() => expect(createSubGraphMock).toHaveBeenCalledTimes(1));
    const spec = createSubGraphMock.mock.calls[0][1];
    expect(spec.storedQuery).toBe("tpl");
    expect(spec.semantic).toBeUndefined();
  });

  it("a registered semantic subgraph shows the badge with its binding", async () => {
    subGraphList = [
      {
        name: "close",
        vertexCount: 3,
        edgeCount: 2,
        semantic: {
          embeddingName: "default",
          metric: "Cosine",
          dimension: 2,
          minScore: 0.5,
          patternThresholds: [{ pattern: "start", minScore: 0.6 }],
        },
      },
      { name: "plain", vertexCount: 1, edgeCount: 0 },
    ];
    renderScreen(true);

    const badge = await screen.findByTestId("sg-semantic-badge-close");
    expect(badge).toHaveAttribute("title", expect.stringContaining("Cosine over 'default' (d=2)"));
    expect(badge).toHaveAttribute("title", expect.stringContaining("step start ≥ 0.6"));
    expect(badge).toHaveAttribute("title", expect.stringContaining("bound at creation"));
    expect(screen.queryByTestId("sg-semantic-badge-plain")).not.toBeInTheDocument();
  });
});
