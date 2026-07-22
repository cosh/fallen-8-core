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
import { resetInstanceStoresForTests } from "../src/state/instanceStore";

/**
 * Subgraph screen semantic wiring (feature element-embeddings / studio-semantics): the
 * inline path attaches the semantic block; stored mode disables the block and NEVER builds
 * or validates it (so a valid stored-template create is not blocked by a greyed-out block).
 * Monaco is mocked (the delegate slots pull it in transitively).
 */

vi.mock("../src/delegate/monacoSetup", () => ({ setupMonaco: () => {}, monaco: {} }));
vi.mock("@monaco-editor/react", () => ({
  default: ({ value }: { value: string }) => <textarea data-testid="mock-editor" value={value} readOnly />,
}));

const createSubGraphMock =
  vi.fn<(i: InstanceConfig, spec: SubGraphSpecification, from?: string) => Promise<SubGraphSummary | null>>();

vi.mock("../src/api/endpoints", async (importOriginal) => {
  const original = await importOriginal<typeof import("../src/api/endpoints")>();
  return {
    ...original,
    listSubGraphSummaries: async () => [] as SubGraphSummary[],
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
  createSubGraphMock.mockReset().mockResolvedValue({ name: "s", vertexCount: 0, edgeCount: 0 });
});

describe("subgraph semantic block", () => {
  it("attaches the semantic spec in inline mode and owns the vertex pre-filter slot", async () => {
    const user = userEvent.setup();
    renderScreen(true);
    await user.type(screen.getByTestId("sg-name"), "close");
    await user.click(screen.getByTestId("sg-semantic-enable"));
    await user.type(screen.getByTestId("sg-sem-vector"), "1, 0");
    await user.click(screen.getByTestId("sg-sem-minscore-enable"));

    // The top-level vertexFilter slot is now owned by minScore (rendered inert w/ reason).
    expect(screen.getByText(/owned by semantic minScore/)).toBeInTheDocument();

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

  it("costBySimilarity is not offered on the subgraph screen (path-only)", async () => {
    const user = userEvent.setup();
    renderScreen(true);
    await user.click(screen.getByTestId("sg-semantic-enable"));
    expect(screen.queryByTestId("sg-sem-cost")).not.toBeInTheDocument();
  });

  it("disables the block in stored mode and never builds/validates it (a valid stored create still works)", async () => {
    const user = userEvent.setup();
    renderScreen(true);
    await user.type(screen.getByTestId("sg-name"), "fromTpl");

    // Enable the block and leave it INVALID (empty vector) in inline mode...
    await user.click(screen.getByTestId("sg-semantic-enable"));

    // ...then switch to a stored template: the block goes inert.
    await user.click(screen.getByTestId("filter-source-stored"));
    expect(screen.getByTestId("sg-semantic-disabled")).toBeInTheDocument();
    await user.selectOptions(screen.getByTestId("stored-query-select"), "tpl");

    await user.click(screen.getByTestId("sg-create"));
    // No throw from the stale invalid semantic block — stored mode ignores it entirely.
    await waitFor(() => expect(createSubGraphMock).toHaveBeenCalledTimes(1));
    const spec = createSubGraphMock.mock.calls[0][1];
    expect(spec.storedQuery).toBe("tpl");
    expect(spec.semantic).toBeUndefined();
  });
});
