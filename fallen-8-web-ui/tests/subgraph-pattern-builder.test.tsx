import { beforeEach, describe, expect, it, vi } from "vitest";
import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import type { InstanceConfig } from "../src/instances/types";
import type {
  StatusREST,
  SubGraphSpecification,
  SubGraphSummary,
} from "../src/api/types";
import { resetInstanceStoresForTests } from "../src/state/instanceStore";

/**
 * Subgraph pattern builder (feature structural-decomposition, Phase 3 pinning): the
 * step-by-step builder's draft→request wire shape for a non-semantic run (builder keys
 * stripped, per-type fields normalized, fromSubGraph as a query-param argument) and the
 * alternation guard wiring — pinned BEFORE the builder panels move out of the screen file.
 * Monaco is mocked (the delegate slots pull it in transitively); the semantic block is
 * pinned in subgraph-semantic.test.tsx.
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
    listStoredQueries: async () => [],
    createSubGraph: (i: InstanceConfig, spec: SubGraphSpecification, from?: string) =>
      createSubGraphMock(i, spec, from),
  };
});

import { SubgraphScreen } from "../src/screens/SubgraphScreen";

const STATUS: StatusREST = {
  vertexCount: 0,
  edgeCount: 0,
  usedMemory: 0,
  indices: [],
  availableIndexPlugins: [],
  availablePathPlugins: [],
  availableAnalyticsPlugins: [],
  availableServicePlugins: [],
};

function renderScreen() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  client.setQueryData(["local", "status"], STATUS);
  return render(
    <QueryClientProvider client={client}>
      <SubgraphScreen />
    </QueryClientProvider>,
  );
}

/** The step inputs carry generated ids (pn-/pd-/pmin-/pmax-<key>); address them by index. */
function stepInput(container: HTMLElement, prefix: string, index = 0): HTMLElement {
  const matches = container.querySelectorAll(`[id^="${prefix}-"]`);
  expect(matches.length).toBeGreaterThan(index);
  return matches[index] as HTMLElement;
}

beforeEach(() => {
  resetInstanceStoresForTests();
  localStorage.clear();
  createSubGraphMock
    .mockReset()
    .mockResolvedValue({ name: "s", vertexCount: 2, edgeCount: 1 });
});

describe("pattern builder wire shape", () => {
  it("sends normalized steps: no builder key, no direction on Vertex, no lengths on Edge", async () => {
    const user = userEvent.setup();
    const { container } = renderScreen();

    await user.type(screen.getByTestId("sg-name"), "pair");
    await user.click(screen.getByTestId("add-vertex-step"));
    await user.click(screen.getByTestId("add-edge-step"));
    await user.type(stepInput(container, "pn", 0), "start");
    await user.selectOptions(stepInput(container, "pd", 0), "IncomingEdge");
    await user.click(screen.getByTestId("sg-create"));

    await waitFor(() => expect(createSubGraphMock).toHaveBeenCalledTimes(1));
    const spec = createSubGraphMock.mock.calls[0][1];
    // toEqual treats undefined-valued keys as absent — exactly the JSON wire shape. A
    // leftover builder `key` or a direction on the Vertex step would fail this.
    expect(spec).toEqual({
      name: "pair",
      patterns: [
        { type: "Vertex", patternName: "start" },
        { type: "Edge", direction: "IncomingEdge" },
      ],
    });
    // No nesting scope: fromSubGraph must not travel at all.
    expect(createSubGraphMock.mock.calls[0][2]).toBeUndefined();
  });

  it("keeps min/max only on VariableLengthEdge steps", async () => {
    const user = userEvent.setup();
    const { container } = renderScreen();

    await user.type(screen.getByTestId("sg-name"), "reach");
    await user.click(screen.getByTestId("add-vertex-step"));
    await user.click(screen.getByRole("button", { name: "+ Variable-length edge" }));
    const min = stepInput(container, "pmin", 0);
    const max = stepInput(container, "pmax", 0);
    await user.clear(min);
    await user.type(min, "2");
    await user.clear(max);
    await user.type(max, "4");
    await user.click(screen.getByTestId("sg-create"));

    await waitFor(() => expect(createSubGraphMock).toHaveBeenCalledTimes(1));
    expect(createSubGraphMock.mock.calls[0][1].patterns).toEqual([
      { type: "Vertex" },
      { type: "VariableLengthEdge", direction: "OutgoingEdge", minLength: 2, maxLength: 4 },
    ]);
  });

  it("drops the stale direction when a step's type is switched to Vertex", async () => {
    const user = userEvent.setup();
    const { container } = renderScreen();

    await user.type(screen.getByTestId("sg-name"), "solo");
    await user.click(screen.getByTestId("add-edge-step"));
    await user.selectOptions(stepInput(container, "pd", 0), "UndirectedEdge");
    await user.selectOptions(stepInput(container, "pt", 0), "Vertex");
    await user.click(screen.getByTestId("sg-create"));

    await waitFor(() => expect(createSubGraphMock).toHaveBeenCalledTimes(1));
    // The draft still holds the old direction, but the Vertex step must not wire it.
    expect(createSubGraphMock.mock.calls[0][1].patterns).toEqual([{ type: "Vertex" }]);
  });

  it("trims the name and passes fromSubGraph as the query-param argument", async () => {
    const user = userEvent.setup();
    const { container } = renderScreen();

    await user.type(screen.getByTestId("sg-name"), "  scoped  ");
    await user.type(container.querySelector("#sg-from")!, "base");
    await user.click(screen.getByTestId("sg-create"));

    await waitFor(() => expect(createSubGraphMock).toHaveBeenCalledTimes(1));
    expect(createSubGraphMock.mock.calls[0][1]).toEqual({ name: "scoped", patterns: [] });
    expect(createSubGraphMock.mock.calls[0][2]).toBe("base");
  });
});

describe("sequence guard wiring", () => {
  it("blocks a non-alternating sequence and recovers when the offending step is removed", async () => {
    const user = userEvent.setup();
    renderScreen();

    await user.type(screen.getByTestId("sg-name"), "bad");
    await user.click(screen.getByTestId("add-edge-step"));
    await user.click(screen.getByTestId("add-edge-step"));

    expect(screen.getByTestId("sequence-error")).toHaveTextContent(/Step 2 \(Edge\) must alternate/);
    expect(screen.getByTestId("sg-create")).toBeDisabled();
    await user.click(screen.getByTestId("sg-create"));
    expect(createSubGraphMock).not.toHaveBeenCalled();

    // Removing the second edge step clears the error and re-arms Create.
    await user.click(screen.getAllByRole("button", { name: "Remove" })[1]);
    expect(screen.queryByTestId("sequence-error")).not.toBeInTheDocument();
    expect(screen.getByTestId("sg-create")).toBeEnabled();
  });

  it("blocks a variable-length step whose min exceeds max", async () => {
    const user = userEvent.setup();
    const { container } = renderScreen();

    await user.type(screen.getByTestId("sg-name"), "bad");
    await user.click(screen.getByRole("button", { name: "+ Variable-length edge" }));
    const min = stepInput(container, "pmin", 0);
    await user.clear(min);
    await user.type(min, "9");

    expect(screen.getByTestId("sequence-error")).toHaveTextContent(/minLength \(9\) exceeds maxLength \(3\)/);
    expect(screen.getByTestId("sg-create")).toBeDisabled();
  });
});

describe("create outcome", () => {
  it("reports counts, and names an empty subgraph a valid result", async () => {
    const user = userEvent.setup();
    createSubGraphMock.mockResolvedValue({ name: "hollow", vertexCount: 0, edgeCount: 0 });
    renderScreen();

    await user.type(screen.getByTestId("sg-name"), "hollow");
    await user.click(screen.getByTestId("sg-create"));

    const message = await screen.findByTestId("subgraph-message");
    expect(message).toHaveTextContent("Created 'hollow': 0 vertices, 0 edges.");
    expect(message).toHaveTextContent(/Empty is a valid result/);
  });
});
