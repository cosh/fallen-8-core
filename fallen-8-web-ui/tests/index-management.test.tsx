// MIT License
//
// index-management.test.tsx
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
  IndexAddToSpecification,
  IndexKeySpecification,
  PluginSpecification,
  StatusREST,
  VectorIndexAddSpecification,
} from "../src/api/types";

/**
 * Indexes screen (feature index-workspace): the inventory table (type, capabilities,
 * counts, bound badge), the create panel with per-type options (moved from the Query
 * screen — feature studio-index-discovery), delete behind the typed confirmation with
 * honest bound-vs-content-loss copy, and per-index content management over the full
 * REST surface.
 */

const getStatusMock = vi.fn<(i: InstanceConfig, signal?: AbortSignal) => Promise<StatusREST | null>>();
const createIndexMock = vi.fn<(i: InstanceConfig, spec: PluginSpecification) => Promise<boolean | null>>();
const deleteIndexMock = vi.fn<(i: InstanceConfig, indexId: string) => Promise<boolean | null>>();
const addToIndexMock =
  vi.fn<(i: InstanceConfig, indexId: string, spec: IndexAddToSpecification) => Promise<boolean | null>>();
const removeIndexKeyMock =
  vi.fn<(i: InstanceConfig, indexId: string, key: IndexKeySpecification) => Promise<boolean | null>>();
const removeFromIndexMock =
  vi.fn<(i: InstanceConfig, indexId: string, graphElementId: number) => Promise<boolean | null>>();
const addVectorToIndexMock =
  vi.fn<(i: InstanceConfig, indexId: string, spec: VectorIndexAddSpecification) => Promise<boolean | null>>();

vi.mock("../src/api/endpoints", async (importOriginal) => {
  const original = await importOriginal<typeof import("../src/api/endpoints")>();
  return {
    ...original,
    getStatus: (i: InstanceConfig, signal?: AbortSignal) => getStatusMock(i, signal),
    createIndex: (i: InstanceConfig, spec: PluginSpecification) => createIndexMock(i, spec),
    deleteIndex: (i: InstanceConfig, indexId: string) => deleteIndexMock(i, indexId),
    addToIndex: (i: InstanceConfig, indexId: string, spec: IndexAddToSpecification) =>
      addToIndexMock(i, indexId, spec),
    removeIndexKey: (i: InstanceConfig, indexId: string, key: IndexKeySpecification) =>
      removeIndexKeyMock(i, indexId, key),
    removeFromIndex: (i: InstanceConfig, indexId: string, graphElementId: number) =>
      removeFromIndexMock(i, indexId, graphElementId),
    addVectorToIndex: (i: InstanceConfig, indexId: string, spec: VectorIndexAddSpecification) =>
      addVectorToIndexMock(i, indexId, spec),
  };
});

import { IndexesScreen } from "../src/screens/IndexesScreen";

const STATUS: StatusREST = {
  vertexCount: 0,
  edgeCount: 0,
  usedMemory: 0,
  indices: [
    {
      indexId: "nameIndex",
      pluginType: "DictionaryIndex",
      capabilities: ["equality"],
      keys: 2,
      values: 3,
    },
    {
      indexId: "embeddings",
      pluginType: "VectorIndex",
      capabilities: ["vector"],
      keys: 1,
      values: 1,
    },
    {
      indexId: "geo",
      pluginType: "SpatialIndex",
      capabilities: ["spatial"],
      keys: 4,
      values: 4,
    },
    {
      indexId: "docs",
      pluginType: "RegExIndex",
      capabilities: ["equality", "fulltext"],
      keys: 1,
      values: 1,
    },
    {
      indexId: "boundIdx",
      pluginType: "VectorIndex",
      embeddingName: "default",
      model: "bge-micro-v2#384#Cosine",
      capabilities: ["vector"],
      keys: 0,
      values: 0,
    },
  ],
  availableIndexPlugins: [
    "DictionaryIndex",
    "RangeIndex",
    "RegExIndex",
    "SingleValueIndex",
    "SpatialIndex",
    "VectorIndex",
  ],
  availablePathPlugins: [],
  availableAnalyticsPlugins: [],
  availableServicePlugins: [],
};

function renderScreen() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={client}>
      <IndexesScreen />
    </QueryClientProvider>,
  );
}

/** The field starts as the free-form fallback until /status resolves — wait for the swap. */
async function findTypeSelect() {
  await waitFor(() => expect(screen.getByTestId("index-type").tagName).toBe("SELECT"));
  return screen.getByTestId("index-type");
}

async function selectRow(indexId: string) {
  const user = userEvent.setup();
  renderScreen();
  await waitFor(() =>
    expect(screen.getByTestId(`index-row-${indexId}`)).toBeInTheDocument(),
  );
  await user.click(screen.getByTestId(`index-row-${indexId}`));
  await waitFor(() => expect(screen.getByTestId("index-content")).toBeInTheDocument());
  return user;
}

beforeEach(() => {
  getStatusMock.mockReset().mockResolvedValue(STATUS);
  createIndexMock.mockReset().mockResolvedValue(true);
  deleteIndexMock.mockReset().mockResolvedValue(true);
  addToIndexMock.mockReset().mockResolvedValue(true);
  removeIndexKeyMock.mockReset().mockResolvedValue(true);
  removeFromIndexMock.mockReset().mockResolvedValue(true);
  addVectorToIndexMock.mockReset().mockResolvedValue(true);
});

describe("inventory table", () => {
  it("lists every index with type, capabilities, counts and the bound badge", async () => {
    renderScreen();
    await waitFor(() => expect(screen.getByTestId("index-inventory")).toBeInTheDocument());

    const nameRow = within(screen.getByTestId("index-row-nameIndex"));
    expect(nameRow.getByText("DictionaryIndex")).toBeInTheDocument();
    expect(nameRow.getByText("equality")).toBeInTheDocument();
    expect(nameRow.getByText("2")).toBeInTheDocument();
    expect(nameRow.getByText("3")).toBeInTheDocument();

    expect(screen.getByTestId("index-bound-boundIdx")).toHaveTextContent("bound:default");
    expect(screen.queryByTestId("index-bound-nameIndex")).not.toBeInTheDocument();
    // The declared model identity (embedding-provider contract) is shown, not swallowed.
    expect(screen.getByTestId("index-model-boundIdx")).toHaveTextContent(
      "bge-micro-v2#384#Cosine",
    );
  });

  it("derives capabilities from the plugin type when the server reports none", async () => {
    getStatusMock.mockResolvedValue({
      ...STATUS,
      indices: [{ indexId: "old", pluginType: "RangeIndex" }],
    });
    renderScreen();
    await waitFor(() => expect(screen.getByTestId("index-row-old")).toBeInTheDocument());
    expect(
      within(screen.getByTestId("index-row-old")).getByText("equality · range"),
    ).toBeInTheDocument();
  });

  it("explains an inventory-less (older) server instead of claiming emptiness", async () => {
    getStatusMock.mockResolvedValue({ ...STATUS, indices: null });
    renderScreen();
    await waitFor(() =>
      expect(screen.getByTestId("inventory-empty")).toHaveTextContent(/older \/status contract/),
    );
  });

  it("older servers keep a free-form delete path (no rows can ever render)", async () => {
    getStatusMock.mockResolvedValue({ ...STATUS, indices: null });
    const user = userEvent.setup();
    renderScreen();
    const fallback = await screen.findByTestId("fallback-delete");

    await user.type(within(fallback).getByLabelText(/index id/i), "legacy");
    await user.click(within(fallback).getByRole("button", { name: "Delete" }));
    await user.type(screen.getByTestId("confirm-typed"), "local");
    await user.click(screen.getByTestId("confirm-action"));

    await waitFor(() => expect(deleteIndexMock).toHaveBeenCalledTimes(1));
    expect(deleteIndexMock.mock.calls[0][1]).toBe("legacy");
  });
});

// Feature semantic-search-onramp FR-4. An inventory of dictionary indices reads as a complete
// answer to "can this instance search by meaning", which is how an operator with many embedded
// elements concluded the capability did not exist.
describe("the missing-vector-index pointer", () => {
  it("names what a semantic search needs when no index can rank vectors", async () => {
    getStatusMock.mockResolvedValue({
      ...STATUS,
      indices: [{ indexId: "f8i-claims", pluginType: "DictionaryIndex", capabilities: ["equality"] }],
    });
    renderScreen();
    await waitFor(() =>
      expect(screen.getByTestId("no-vector-index-note")).toHaveTextContent(/semantic search/i),
    );
  });

  it("stays out of the way on an instance with no indexes at all", async () => {
    // The empty-inventory paragraph already says "create one below", so adding a second
    // sentence that says it again is noise in the first state a newcomer sees. The pointer earns
    // its keep only where other families are present and this one is missing.
    getStatusMock.mockResolvedValue({ ...STATUS, indices: [] });
    renderScreen();
    await waitFor(() =>
      expect(screen.getByTestId("inventory-empty")).toHaveTextContent(/No indexes on this instance/),
    );
    expect(screen.queryByTestId("no-vector-index-note")).not.toBeInTheDocument();
  });

  it("stays quiet once a vector index exists", async () => {
    renderScreen();
    await waitFor(() => expect(screen.getByTestId("index-inventory")).toBeInTheDocument());
    expect(screen.queryByTestId("no-vector-index-note")).not.toBeInTheDocument();
  });

  it("counts an index of unknown capability as one that could rank, rather than claiming", async () => {
    // A pre-capabilities server plus a third-party plugin: indexCapabilities errs toward every
    // family, so the screen cannot know this one is not a vector index and must not say so.
    getStatusMock.mockResolvedValue({
      ...STATUS,
      indices: [{ indexId: "acme", pluginType: "AcmeIndex" }],
    });
    renderScreen();
    await waitFor(() => expect(screen.getByTestId("index-row-acme")).toBeInTheDocument());
    expect(screen.queryByTestId("no-vector-index-note")).not.toBeInTheDocument();
  });

  it("makes no claim when the server does not report an inventory", async () => {
    // Nothing is known about what indexes exist, so "there is no vector index" is not a
    // statement this screen is entitled to make.
    getStatusMock.mockResolvedValue({ ...STATUS, indices: null });
    renderScreen();
    await waitFor(() => expect(screen.getByTestId("inventory-empty")).toBeInTheDocument());
    expect(screen.queryByTestId("no-vector-index-note")).not.toBeInTheDocument();
  });
});

describe("plugin-type dropdown", () => {
  it("lists the server's available index plugins, DictionaryIndex preselected", async () => {
    renderScreen();
    const select = await findTypeSelect();
    expect(
      [...select.querySelectorAll("option")].map((o) => o.value),
    ).toEqual(STATUS.availableIndexPlugins);
    expect(select).toHaveValue("DictionaryIndex");
  });

  it("falls back to a free-form input when the server lists no plugins", async () => {
    getStatusMock.mockResolvedValue({ ...STATUS, availableIndexPlugins: [] });
    renderScreen();
    await waitFor(() => expect(getStatusMock).toHaveBeenCalled());
    const field = screen.getByTestId("index-type");
    expect(field.tagName).toBe("INPUT");
  });
});

describe("per-type creation options", () => {
  it("no-option types show the hint and send no pluginOptions", async () => {
    const user = userEvent.setup();
    renderScreen();
    await findTypeSelect();
    expect(screen.getByTestId("no-options-note")).toBeInTheDocument();

    await user.type(screen.getByLabelText(/index id/i), "myIndex");
    await user.click(screen.getByRole("button", { name: "Create" }));
    await waitFor(() => expect(createIndexMock).toHaveBeenCalledTimes(1));
    expect(createIndexMock.mock.calls[0][1]).toEqual({
      uniqueId: "myIndex",
      pluginType: "DictionaryIndex",
      pluginOptions: undefined,
    });
  });

  it("VectorIndex shows dimension/metric and sends them as typed literals", async () => {
    const user = userEvent.setup();
    renderScreen();
    const select = await findTypeSelect();
    await user.selectOptions(select, "VectorIndex");
    expect(screen.queryByTestId("no-options-note")).not.toBeInTheDocument();

    await user.type(screen.getByLabelText(/index id/i), "vec");
    await user.click(screen.getByRole("button", { name: "Create" }));
    await waitFor(() => expect(createIndexMock).toHaveBeenCalledTimes(1));
    expect(createIndexMock.mock.calls[0][1]).toEqual({
      uniqueId: "vec",
      pluginType: "VectorIndex",
      pluginOptions: {
        dimension: {
          propertyId: "dimension",
          propertyValue: "384",
          fullQualifiedTypeName: "System.Int32",
        },
        metric: {
          propertyId: "metric",
          propertyValue: "Cosine",
          fullQualifiedTypeName: "System.String",
        },
      },
    });
  });

  it("SpatialIndex disables Create with the honest note", async () => {
    const user = userEvent.setup();
    renderScreen();
    const select = await findTypeSelect();
    await user.selectOptions(select, "SpatialIndex");
    await user.type(screen.getByLabelText(/index id/i), "geo2");

    expect(screen.getByRole("button", { name: "Create" })).toBeDisabled();
    expect(screen.getByTestId("spatial-create-note")).toBeInTheDocument();
    expect(createIndexMock).not.toHaveBeenCalled();
  });
});

// The other half of the shared guard in lib/vectorIndexCreate.ts. Only the Query screen's
// on-ramp twin was pinned, and this panel had the same hole: an emptied dimension travelled as
// "" against System.Int32, which the server cannot convert.
describe("the shared dimension guard", () => {
  it("refuses to submit a dimension the engine cannot take", async () => {
    const user = userEvent.setup();
    renderScreen();
    await findTypeSelect();
    await user.selectOptions(screen.getByTestId("index-type"), "VectorIndex");
    await user.type(screen.getByLabelText(/index id/i), "vec");
    expect(screen.getByRole("button", { name: "Create" })).toBeEnabled();

    await user.clear(screen.getByTestId("vector-dimension-opt"));
    expect(screen.getByRole("button", { name: "Create" })).toBeDisabled();
    expect(screen.getByTestId("vector-dimension-invalid")).toBeInTheDocument();

    await user.type(screen.getByTestId("vector-dimension-opt"), "9999");
    expect(screen.getByRole("button", { name: "Create" })).toBeDisabled();

    await user.clear(screen.getByTestId("vector-dimension-opt"));
    await user.type(screen.getByTestId("vector-dimension-opt"), "768");
    expect(screen.getByRole("button", { name: "Create" })).toBeEnabled();
    expect(screen.queryByTestId("vector-dimension-invalid")).not.toBeInTheDocument();
    expect(createIndexMock).not.toHaveBeenCalled();
  });

  it("leaves a non-vector create alone, which has no dimension at all", async () => {
    const user = userEvent.setup();
    renderScreen();
    await findTypeSelect();
    await user.type(screen.getByLabelText(/index id/i), "byName");
    expect(screen.queryByTestId("vector-dimension-invalid")).not.toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Create" })).toBeEnabled();
  });
});

describe("create outcome", () => {
  it("a false answer reads as NOT created and does not refetch the inventory", async () => {
    const user = userEvent.setup();
    createIndexMock.mockResolvedValue(false);
    renderScreen();
    await findTypeSelect();
    const statusFetches = getStatusMock.mock.calls.length;

    await user.type(screen.getByLabelText(/index id/i), "dup");
    await user.click(screen.getByRole("button", { name: "Create" }));
    await waitFor(() =>
      expect(screen.getByText(/was NOT created/)).toBeInTheDocument(),
    );
    expect(getStatusMock.mock.calls.length).toBe(statusFetches);
  });

  it("a successful create refetches /status so the inventory sees the new index", async () => {
    const user = userEvent.setup();
    renderScreen();
    await findTypeSelect();
    const statusFetches = getStatusMock.mock.calls.length;

    await user.type(screen.getByLabelText(/index id/i), "fresh");
    await user.click(screen.getByRole("button", { name: "Create" }));
    await waitFor(() =>
      expect(screen.getByText("Index 'fresh' created.")).toBeInTheDocument(),
    );
    await waitFor(() =>
      expect(getStatusMock.mock.calls.length).toBeGreaterThan(statusFetches),
    );
  });
});

describe("delete confirmation", () => {
  it("arms only after typing the instance name, states content loss, then deletes", async () => {
    const user = userEvent.setup();
    renderScreen();
    await waitFor(() =>
      expect(screen.getByTestId("index-delete-nameIndex")).toBeInTheDocument(),
    );

    await user.click(screen.getByTestId("index-delete-nameIndex"));
    expect(screen.getByText(/drops the index AND its content/)).toBeInTheDocument();
    expect(screen.getByTestId("confirm-action")).toBeDisabled();

    await user.type(screen.getByTestId("confirm-typed"), "local");
    expect(screen.getByTestId("confirm-action")).toBeEnabled();
    await user.click(screen.getByTestId("confirm-action"));

    await waitFor(() => expect(deleteIndexMock).toHaveBeenCalledTimes(1));
    expect(deleteIndexMock.mock.calls[0][1]).toBe("nameIndex");
    await waitFor(() =>
      expect(screen.getByText("Index 'nameIndex' deleted.")).toBeInTheDocument(),
    );
  });

  it("tells a bound index apart: recreation rebuilds its content", async () => {
    const user = userEvent.setup();
    renderScreen();
    await waitFor(() =>
      expect(screen.getByTestId("index-delete-boundIdx")).toBeInTheDocument(),
    );

    await user.click(screen.getByTestId("index-delete-boundIdx"));
    expect(screen.getByText(/rebuilds its content automatically/)).toBeInTheDocument();
  });

  it("cancel closes the dialog without deleting", async () => {
    const user = userEvent.setup();
    renderScreen();
    await waitFor(() =>
      expect(screen.getByTestId("index-delete-nameIndex")).toBeInTheDocument(),
    );

    await user.click(screen.getByTestId("index-delete-nameIndex"));
    await user.click(screen.getByRole("button", { name: "Cancel" }));
    expect(deleteIndexMock).not.toHaveBeenCalled();
  });

  it("a cancelled typed name never pre-arms the next delete target", async () => {
    const user = userEvent.setup();
    renderScreen();
    await waitFor(() =>
      expect(screen.getByTestId("index-delete-nameIndex")).toBeInTheDocument(),
    );

    // Type the instance name for target A, then cancel via the button (which bypasses
    // Radix onOpenChange) — reopening for target B must start disarmed.
    await user.click(screen.getByTestId("index-delete-nameIndex"));
    await user.type(screen.getByTestId("confirm-typed"), "local");
    await user.click(screen.getByRole("button", { name: "Cancel" }));

    await user.click(screen.getByTestId("index-delete-embeddings"));
    expect(screen.getByTestId("confirm-typed")).toHaveValue("");
    expect(screen.getByTestId("confirm-action")).toBeDisabled();
  });
});

describe("content management", () => {
  it("key-literal indexes: add element sends the typed key on the wire shape", async () => {
    const user = await selectRow("nameIndex");

    await user.type(
      within(screen.getByTestId("content-add")).getByLabelText(/element id/i),
      "7",
    );
    await user.type(screen.getByTestId("content-add-key-value"), "John");
    await user.click(screen.getByRole("button", { name: "Add element" }));

    await waitFor(() => expect(addToIndexMock).toHaveBeenCalledTimes(1));
    expect(addToIndexMock.mock.calls[0][1]).toBe("nameIndex");
    expect(addToIndexMock.mock.calls[0][2]).toEqual({
      graphElementId: 7,
      key: { propertyValue: "John", fullQualifiedTypeName: "System.String" },
    });
  });

  it("remove key and remove element hit their endpoints with honest false answers", async () => {
    removeIndexKeyMock.mockResolvedValue(false);
    const user = await selectRow("nameIndex");

    await user.type(screen.getByTestId("content-remove-key-value"), "ghost");
    await user.click(screen.getByRole("button", { name: "Remove key" }));
    await waitFor(() => expect(removeIndexKeyMock).toHaveBeenCalledTimes(1));
    expect(removeIndexKeyMock.mock.calls[0][2]).toEqual({
      propertyValue: "ghost",
      fullQualifiedTypeName: "System.String",
    });
    // The server's false covers both a missing key and a concurrently deleted index —
    // the message must not assert more than that.
    await waitFor(() =>
      expect(
        screen.getByText("Nothing removed — the key (or the index) was not found."),
      ).toBeInTheDocument(),
    );

    await user.type(
      within(screen.getByTestId("content-remove-element")).getByLabelText(/element id/i),
      "9",
    );
    await user.click(screen.getByRole("button", { name: "Remove element" }));
    await waitFor(() => expect(removeFromIndexMock).toHaveBeenCalledTimes(1));
    expect(removeFromIndexMock.mock.calls[0][1]).toBe("nameIndex");
    expect(removeFromIndexMock.mock.calls[0][2]).toBe(9);
  });

  it("vector indexes get the vector-add form (property mode payload)", async () => {
    const user = await selectRow("embeddings");
    expect(screen.getByTestId("vector-add")).toBeInTheDocument();
    // No typed-key forms for a vector index: float[] keys are not wire literals.
    expect(screen.queryByTestId("content-add")).not.toBeInTheDocument();

    await user.type(
      within(screen.getByTestId("vector-add")).getByLabelText(/element id/i),
      "3",
    );
    await user.click(screen.getByRole("button", { name: "Add vector" }));

    await waitFor(() => expect(addVectorToIndexMock).toHaveBeenCalledTimes(1));
    expect(addVectorToIndexMock.mock.calls[0][1]).toBe("embeddings");
    expect(addVectorToIndexMock.mock.calls[0][2]).toEqual({
      graphElementId: 3,
      propertyId: "embedding",
    });
  });

  it("fulltext-family indexes lock keys to strings (other types are silent no-ops server-side)", async () => {
    const user = await selectRow("docs");

    // No type dropdown: RegExIndex.AddOrUpdate ignores every non-string key, so offering
    // Int32 etc. would report success while indexing nothing.
    expect(screen.queryByLabelText("key type")).not.toBeInTheDocument();
    await user.type(
      within(screen.getByTestId("content-add")).getByLabelText(/element id/i),
      "5",
    );
    await user.type(screen.getByTestId("content-add-key-value"), "needle");
    await user.click(screen.getByRole("button", { name: "Add element" }));

    await waitFor(() => expect(addToIndexMock).toHaveBeenCalledTimes(1));
    expect(addToIndexMock.mock.calls[0][2]).toEqual({
      graphElementId: 5,
      key: { propertyValue: "needle", fullQualifiedTypeName: "System.String" },
    });
  });

  it("spatial indexes offer only element removal, with the reason", async () => {
    await selectRow("geo");
    expect(screen.getByTestId("spatial-content-note")).toBeInTheDocument();
    expect(screen.queryByTestId("content-add")).not.toBeInTheDocument();
    expect(screen.queryByTestId("vector-add")).not.toBeInTheDocument();
    expect(screen.getByTestId("content-remove-element")).toBeInTheDocument();
  });

  it("a bound index gets no content forms, only the self-maintained note", async () => {
    await selectRow("boundIdx");
    expect(screen.getByTestId("bound-content-note")).toBeInTheDocument();
    expect(screen.queryByTestId("vector-add")).not.toBeInTheDocument();
    expect(screen.queryByTestId("content-remove-element")).not.toBeInTheDocument();
  });
});

describe("the create form prefills from the embedding provider, because guessing is worse", () => {
  const WITH_PROVIDER: StatusREST = {
    ...STATUS,
    embedding: {
      enabled: true,
      backend: "Ollama",
      modelName: "bge-m3",
      modelVersion: null,
      dimension: 1024,
      intendedMetric: "Cosine",
      loaded: false,
    },
  };

  it("sends the PROVIDER's dimension and metric, not the hardcoded 384", async () => {
    getStatusMock.mockResolvedValue(WITH_PROVIDER);
    const user = userEvent.setup();
    renderScreen();
    const select = await findTypeSelect();
    await user.selectOptions(select, "VectorIndex");

    await waitFor(() =>
      expect(screen.getByTestId("vector-dimension-opt")).toHaveValue(1024),
    );
    await user.type(screen.getByLabelText(/index id/i), "vec");
    await user.click(screen.getByRole("button", { name: "Create" }));

    await waitFor(() => expect(createIndexMock).toHaveBeenCalledTimes(1));
    // Accepting 384 against the shipped 1024 provider produced an index that 409s every later
    // text-in embed and search, and made the integration's own summary write fail rather than degrade.
    const options = createIndexMock.mock.calls[0][1].pluginOptions!;
    expect(options.dimension.propertyValue).toBe("1024");
    expect(options.metric.propertyValue).toBe("Cosine");
  });

  it("names the provider it prefilled from, so the number is not a mystery", async () => {
    getStatusMock.mockResolvedValue(WITH_PROVIDER);
    const user = userEvent.setup();
    renderScreen();
    await user.selectOptions(await findTypeSelect(), "VectorIndex");

    expect(await screen.findByTestId("vector-provider-note")).toHaveTextContent(/bge-m3/);
    expect(screen.getByTestId("vector-provider-note")).toHaveTextContent(/1024/);
  });

  it("lets a typed dimension win, because a late status must not clobber an edit", async () => {
    getStatusMock.mockResolvedValue(WITH_PROVIDER);
    const user = userEvent.setup();
    renderScreen();
    await user.selectOptions(await findTypeSelect(), "VectorIndex");
    await waitFor(() =>
      expect(screen.getByTestId("vector-dimension-opt")).toHaveValue(1024),
    );

    await user.clear(screen.getByTestId("vector-dimension-opt"));
    await user.type(screen.getByTestId("vector-dimension-opt"), "768");
    await user.type(screen.getByLabelText(/index id/i), "vec");
    await user.click(screen.getByRole("button", { name: "Create" }));

    await waitFor(() => expect(createIndexMock).toHaveBeenCalledTimes(1));
    expect(createIndexMock.mock.calls[0][1].pluginOptions!.dimension.propertyValue).toBe("768");
  });

  it("says the numbers are defaults when the provider is off, rather than implying they are its", async () => {
    getStatusMock.mockResolvedValue({
      ...STATUS,
      embedding: {
        enabled: false,
        backend: null,
        modelName: null,
        modelVersion: null,
        dimension: 0,
        intendedMetric: null,
        loaded: false,
      },
    });
    const user = userEvent.setup();
    renderScreen();
    await user.selectOptions(await findTypeSelect(), "VectorIndex");

    expect(await screen.findByTestId("vector-provider-off")).toHaveTextContent(
      /provider is off/,
    );
    expect(screen.getByTestId("vector-dimension-opt")).toHaveValue(384);
  });

  it("says so when the server reports no provider block at all", async () => {
    const user = userEvent.setup();
    renderScreen();
    await user.selectOptions(await findTypeSelect(), "VectorIndex");

    expect(await screen.findByTestId("vector-provider-off")).toHaveTextContent(
      /reports no embedding provider/,
    );
  });
});
