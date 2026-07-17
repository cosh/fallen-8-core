import { beforeEach, describe, expect, it, vi } from "vitest";
import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import type { InstanceConfig } from "../src/instances/types";
import type { PluginSpecification, StatusREST } from "../src/api/types";

/**
 * Index discovery on the Query screen (feature studio-index-discovery): the plugin-type
 * dropdown fed by /status plugin discovery, the live index-id datalist, per-type creation
 * options (vector options / no-options hint / spatial create gating), and the honest
 * created-vs-not-created message on the server's boolean answer.
 */

const getStatusMock = vi.fn<(i: InstanceConfig, signal?: AbortSignal) => Promise<StatusREST | null>>();
const createIndexMock = vi.fn<(i: InstanceConfig, spec: PluginSpecification) => Promise<boolean | null>>();
const deleteIndexMock = vi.fn<(i: InstanceConfig, indexId: string) => Promise<void>>();

vi.mock("../src/api/endpoints", async (importOriginal) => {
  const original = await importOriginal<typeof import("../src/api/endpoints")>();
  return {
    ...original,
    getStatus: (i: InstanceConfig, signal?: AbortSignal) => getStatusMock(i, signal),
    createIndex: (i: InstanceConfig, spec: PluginSpecification) => createIndexMock(i, spec),
    deleteIndex: (i: InstanceConfig, indexId: string) => deleteIndexMock(i, indexId),
  };
});

import { QueryScreen } from "../src/screens/QueryScreen";

const STATUS: StatusREST = {
  vertexCount: 0,
  edgeCount: 0,
  usedMemory: 0,
  indices: [
    { indexId: "nameIndex", pluginType: "DictionaryIndex" },
    { indexId: "embeddings", pluginType: "VectorIndex" },
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
      <QueryScreen />
    </QueryClientProvider>,
  );
}

/** The field starts as the free-form fallback until /status resolves — wait for the swap. */
async function findTypeSelect() {
  await waitFor(() => expect(screen.getByTestId("index-type").tagName).toBe("SELECT"));
  return screen.getByTestId("index-type");
}

beforeEach(() => {
  getStatusMock.mockReset().mockResolvedValue(STATUS);
  createIndexMock.mockReset().mockResolvedValue(true);
  deleteIndexMock.mockReset().mockResolvedValue(undefined);
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

describe("live index-id suggestions", () => {
  it("feeds the shared datalist from the /status inventory without a shape snapshot", async () => {
    const { container } = renderScreen();
    await waitFor(() => {
      const values = [
        ...container.querySelectorAll("#shape-index-ids option"),
      ].map((o) => (o as HTMLOptionElement).value);
      expect(values).toEqual(["nameIndex", "embeddings"]);
    });
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

  it("SpatialIndex disables Create with the honest note; Delete stays available", async () => {
    const user = userEvent.setup();
    renderScreen();
    const select = await findTypeSelect();
    await user.selectOptions(select, "SpatialIndex");
    await user.type(screen.getByLabelText(/index id/i), "geo");

    expect(screen.getByRole("button", { name: "Create" })).toBeDisabled();
    expect(screen.getByTestId("spatial-create-note")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Delete" })).toBeEnabled();
    expect(createIndexMock).not.toHaveBeenCalled();
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

  it("a successful create refetches /status so the dropdowns see the new index", async () => {
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
