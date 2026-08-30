// MIT License
//
// plugins-panel.test.tsx
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
import { render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import type { InstanceConfig } from "../src/instances/types";
import type {
  GraphFunctionResultREST,
  PluginDetailREST,
  PluginSummaryREST,
  PluginValidationResult,
} from "../src/api/types";

/**
 * Plugins panel (feature plugin-registration): the registry management home — list with the
 * category/contract/compileState columns, the Run affordance only for a runnable function,
 * the delete flow behind the typed confirmation, and the authoring editor loading a
 * per-category scaffold. Monaco is mocked to a textarea (as in delegate-editor.test.tsx).
 */

vi.mock("../src/delegate/monacoSetup", () => ({ setupMonaco: () => {}, monaco: {} }));
vi.mock("@monaco-editor/react", () => ({
  default: ({
    value,
    onChange,
  }: {
    value: string;
    onChange?: (v: string | undefined) => void;
  }) => (
    <textarea
      data-testid="mock-editor"
      value={value}
      onChange={(e) => onChange?.(e.target.value)}
    />
  ),
}));

const listPluginsMock = vi.fn<(i: InstanceConfig, s?: AbortSignal) => Promise<PluginSummaryREST[] | null>>();
const getPluginMock = vi.fn<(i: InstanceConfig, n: string, s?: AbortSignal) => Promise<PluginDetailREST | null>>();
const deletePluginMock = vi.fn<(i: InstanceConfig, n: string) => Promise<void | null>>();
const invokeGraphFunctionMock =
  vi.fn<(i: InstanceConfig, n: string, p?: Record<string, string>) => Promise<GraphFunctionResultREST | null>>();
const validatePluginMock = vi.fn<(...a: unknown[]) => Promise<PluginValidationResult | null>>();
const registerAlgorithmPluginMock = vi.fn<(...a: unknown[]) => Promise<PluginSummaryREST | null>>();
const registerFunctionPluginMock = vi.fn<(...a: unknown[]) => Promise<PluginSummaryREST | null>>();

vi.mock("../src/api/endpoints", async (importOriginal) => {
  const original = await importOriginal<typeof import("../src/api/endpoints")>();
  return {
    ...original,
    listPlugins: (i: InstanceConfig, s?: AbortSignal) => listPluginsMock(i, s),
    getPlugin: (i: InstanceConfig, n: string, s?: AbortSignal) => getPluginMock(i, n, s),
    deletePlugin: (i: InstanceConfig, n: string) => deletePluginMock(i, n),
    invokeGraphFunction: (i: InstanceConfig, n: string, p?: Record<string, string>) =>
      invokeGraphFunctionMock(i, n, p),
    validatePlugin: (...a: unknown[]) => validatePluginMock(...a),
    registerAlgorithmPlugin: (...a: unknown[]) => registerAlgorithmPluginMock(...a),
    registerFunctionPlugin: (...a: unknown[]) => registerFunctionPluginMock(...a),
  };
});

import type { NlChatResult } from "../src/delegate/nl/generate";

const chatMock = vi.fn<(...args: unknown[]) => Promise<NlChatResult>>();
vi.mock("../src/delegate/nl/generate", async (importOriginal) => {
  const original = await importOriginal<typeof import("../src/delegate/nl/generate")>();
  return { ...original, generateChat: (...a: unknown[]) => chatMock(...a) };
});

import { PluginsPanel } from "../src/components/PluginsPanel";

const PLUGINS: PluginSummaryREST[] = [
  {
    name: "MyDijkstra",
    category: "Algorithm",
    contract: "Path",
    description: "custom shortest path",
    createdAt: "2026-07-25T10:00:00Z",
    compileState: "Compiled",
  },
  {
    name: "NeighboursOfLabel",
    category: "Function",
    contract: "GraphFunction",
    description: null,
    createdAt: "2026-07-25T10:05:00Z",
    compileState: "Compiled",
  },
  {
    name: "BustedFn",
    category: "Function",
    contract: "GraphFunction",
    description: null,
    createdAt: "2026-07-25T10:06:00Z",
    compileState: "Failed",
  },
];

function renderPanel() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  // The authoring editor's NL panel reads /status for its ambient backend line, keyed on the
  // namespace-bound active instance. Seeded so the row is warm and no jsdom fetch is attempted.
  client.setQueryData(["local/default", "status"], {
    vertexCount: 0,
    edgeCount: 0,
    usedMemory: 0,
    availableIndexPlugins: [],
    availablePathPlugins: [],
    availableAnalyticsPlugins: [],
    availableServicePlugins: [],
    chat: { enabled: true, backend: "Nahil", model: "phi4-f8-mini", loaded: true },
  });
  return render(
    <QueryClientProvider client={client}>
      <PluginsPanel />
    </QueryClientProvider>,
  );
}

beforeEach(() => {
  listPluginsMock.mockReset().mockResolvedValue(PLUGINS);
  getPluginMock.mockReset().mockResolvedValue({
    ...PLUGINS[0],
    sourceCode: "public sealed class MyDijkstra {}",
    compileDiagnostics: null,
  });
  deletePluginMock.mockReset().mockResolvedValue(null);
  invokeGraphFunctionMock.mockReset().mockResolvedValue({ vertices: [], edges: [] });
  validatePluginMock.mockReset().mockResolvedValue({ valid: true, error: null });
  registerAlgorithmPluginMock.mockReset().mockResolvedValue(PLUGINS[0]);
  registerFunctionPluginMock.mockReset().mockResolvedValue(PLUGINS[1]);
  chatMock.mockReset();
});

describe("plugins list", () => {
  it("lists every plugin with category, contract and compile state", async () => {
    renderPanel();
    await waitFor(() =>
      expect(screen.getByTestId("plugin-row-MyDijkstra")).toBeInTheDocument(),
    );
    const row = within(screen.getByTestId("plugin-row-MyDijkstra"));
    expect(row.getByText("Algorithm")).toBeInTheDocument();
    expect(row.getByText("Path")).toBeInTheDocument();
    expect(row.getByText("Compiled")).toBeInTheDocument();
  });

  it("offers Run only for a runnable (Compiled) function, never an algorithm or a Failed one", async () => {
    renderPanel();
    await waitFor(() =>
      expect(screen.getByTestId("plugin-row-NeighboursOfLabel")).toBeInTheDocument(),
    );
    expect(screen.getByTestId("plugin-run-NeighboursOfLabel")).toBeInTheDocument();
    expect(screen.queryByTestId("plugin-run-BustedFn")).not.toBeInTheDocument();
    expect(screen.queryByTestId("plugin-run-MyDijkstra")).not.toBeInTheDocument();
  });

  it("shows read-only source when a row is expanded", async () => {
    const user = userEvent.setup();
    renderPanel();
    await waitFor(() =>
      expect(screen.getByTestId("plugin-row-MyDijkstra")).toBeInTheDocument(),
    );
    await user.click(within(screen.getByTestId("plugin-row-MyDijkstra")).getByRole("button", { name: "Source" }));
    await waitFor(() =>
      expect(screen.getByTestId("plugin-source")).toHaveTextContent(
        "public sealed class MyDijkstra {}",
      ),
    );
  });
});

describe("delete", () => {
  it("deletes behind the typed confirmation", async () => {
    const user = userEvent.setup();
    renderPanel();
    await waitFor(() =>
      expect(screen.getByTestId("plugin-row-MyDijkstra")).toBeInTheDocument(),
    );
    await user.click(within(screen.getByTestId("plugin-row-MyDijkstra")).getByRole("button", { name: "Delete…" }));
    await user.type(screen.getByTestId("confirm-typed"), "local");
    await user.click(screen.getByTestId("confirm-action"));

    await waitFor(() => expect(deletePluginMock).toHaveBeenCalledTimes(1));
    expect(deletePluginMock.mock.calls[0][1]).toBe("MyDijkstra");
  });
});

describe("function runner", () => {
  it("invokes with the entered parameters and renders the returned elements", async () => {
    const user = userEvent.setup();
    invokeGraphFunctionMock.mockResolvedValue({
      vertices: [
        { id: 1, label: "alice", creationDate: "", modificationDate: "" },
        { id: 2, label: "bob", creationDate: "", modificationDate: "" },
      ],
      edges: [{ id: 9, label: "knows", sourceVertex: 1, targetVertex: 2, creationDate: "", modificationDate: "" }],
    });

    renderPanel();
    await waitFor(() =>
      expect(screen.getByTestId("plugin-row-NeighboursOfLabel")).toBeInTheDocument(),
    );
    await user.click(screen.getByTestId("plugin-run-NeighboursOfLabel"));

    await user.type(screen.getByTestId("param-key-0"), "label");
    await user.type(screen.getByTestId("param-value-0"), "person");
    await user.click(screen.getByTestId("plugin-invoke"));

    await waitFor(() => expect(invokeGraphFunctionMock).toHaveBeenCalledTimes(1));
    expect(invokeGraphFunctionMock.mock.calls[0][1]).toBe("NeighboursOfLabel");
    expect(invokeGraphFunctionMock.mock.calls[0][2]).toEqual({ label: "person" });

    const result = within(await screen.findByTestId("plugin-result"));
    expect(result.getByText("alice")).toBeInTheDocument();
    expect(result.getByText("knows")).toBeInTheDocument();
    expect(result.getByText(/vertices \(2\)/)).toBeInTheDocument();
  });
});

describe("authoring editor", () => {
  it("opens the editor and loads a per-category scaffold, switching contract interface", async () => {
    const user = userEvent.setup();
    renderPanel();
    await waitFor(() =>
      expect(screen.getByTestId("plugin-row-MyDijkstra")).toBeInTheDocument(),
    );
    await user.click(screen.getByTestId("register-plugin"));

    // Algorithm/Path scaffold by default.
    const editor = screen.getByTestId("mock-editor") as HTMLTextAreaElement;
    await waitFor(() => expect(editor.value).toContain(": IShortestPathAlgorithm"));

    // Switching to the function category loads the IGraphFunction scaffold.
    await user.selectOptions(screen.getByTestId("plugin-category"), "function");
    await waitFor(() => expect(editor.value).toContain(": IGraphFunction"));
    expect(screen.queryByTestId("plugin-contract")).not.toBeInTheDocument();
  });

  it("names the ambient chat destination instead of the connection's own name", async () => {
    const user = userEvent.setup();
    renderPanel();
    await waitFor(() =>
      expect(screen.getByTestId("plugin-row-MyDijkstra")).toBeInTheDocument(),
    );
    await user.click(screen.getByTestId("register-plugin"));

    expect(screen.getByTestId("plugin-nl-backend-status")).toHaveTextContent(
      "this instance · /chat → Nahil · phi4-f8-mini",
    );
  });

  it("renders the drafting stats it already captured, backend included (FR-6.3)", async () => {
    const user = userEvent.setup();
    chatMock.mockResolvedValueOnce({
      content: "public sealed class Drafted : IGraphFunction {}",
      stats: {
        promptTokens: 640,
        completionTokens: 31,
        durationMs: 2500,
        tokensPerSecond: 12.4,
        backend: "Nahil",
        raw: { backend: "Nahil", eval_count: 31 },
      },
    });
    renderPanel();
    await waitFor(() =>
      expect(screen.getByTestId("plugin-row-MyDijkstra")).toBeInTheDocument(),
    );
    await user.click(screen.getByTestId("register-plugin"));

    await user.type(screen.getByTestId("plugin-nl-intent"), "neighbours of a label");
    await user.click(screen.getByTestId("plugin-nl-generate"));

    await waitFor(() =>
      expect(screen.getByTestId("plugin-nl-attempts")).toHaveTextContent("640→31 tok"),
    );
    const attempts = screen.getByTestId("plugin-nl-attempts");
    expect(attempts).toHaveTextContent("12.4 tok/s · Nahil");
    expect(screen.getByText("raw stats")).toBeInTheDocument();
  });
});
