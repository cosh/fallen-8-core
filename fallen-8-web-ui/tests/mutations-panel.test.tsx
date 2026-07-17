import { beforeEach, describe, expect, it, vi } from "vitest";
import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import type { InstanceConfig } from "../src/instances/types";
import type {
  EdgeSpecification,
  PropertySpecification,
  VertexSpecification,
} from "../src/api/types";

/**
 * Tabbed mutations panel (feature studio-mutations-ux): one tab per mutation type, full
 * REST payloads (creationDate + properties), validation gates on submit, and field
 * state that survives tab switching.
 */

const createVertexMock = vi.fn<(i: InstanceConfig, spec: VertexSpecification) => Promise<void>>();
const createEdgeMock = vi.fn<(i: InstanceConfig, spec: EdgeSpecification) => Promise<void>>();
const setPropertyMock =
  vi.fn<
    (i: InstanceConfig, id: number, propertyId: string, spec: PropertySpecification) => Promise<void>
  >();
const removePropertyMock = vi.fn<(i: InstanceConfig, id: number, propertyId: string) => Promise<void>>();
const removeElementMock = vi.fn<(i: InstanceConfig, id: number) => Promise<void>>();

vi.mock("../src/api/endpoints", async (importOriginal) => {
  const original = await importOriginal<typeof import("../src/api/endpoints")>();
  return {
    ...original,
    createVertex: (i: InstanceConfig, spec: VertexSpecification) => createVertexMock(i, spec),
    createEdge: (i: InstanceConfig, spec: EdgeSpecification) => createEdgeMock(i, spec),
    setProperty: (i: InstanceConfig, id: number, propertyId: string, spec: PropertySpecification) =>
      setPropertyMock(i, id, propertyId, spec),
    removeProperty: (i: InstanceConfig, id: number, propertyId: string) =>
      removePropertyMock(i, id, propertyId),
    removeGraphElement: (i: InstanceConfig, id: number) => removeElementMock(i, id),
  };
});

import { MutationsPanel } from "../src/components/MutationsPanel";

function renderPanel() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={client}>
      <MutationsPanel />
    </QueryClientProvider>,
  );
}

beforeEach(() => {
  createVertexMock.mockReset().mockResolvedValue(undefined);
  createEdgeMock.mockReset().mockResolvedValue(undefined);
  setPropertyMock.mockReset().mockResolvedValue(undefined);
  removePropertyMock.mockReset().mockResolvedValue(undefined);
  removeElementMock.mockReset().mockResolvedValue(undefined);
});

describe("tab structure", () => {
  it("shows one mutation form at a time, vertex first", () => {
    renderPanel();
    expect(screen.getByTestId("mutation-form-vertex")).toBeInTheDocument();
    expect(screen.queryByTestId("mutation-form-edge")).not.toBeInTheDocument();
    expect(screen.queryByTestId("mutation-form-property")).not.toBeInTheDocument();
    expect(screen.queryByTestId("mutation-form-remove")).not.toBeInTheDocument();
  });

  it("switches forms per tab and preserves field state across switches", async () => {
    const user = userEvent.setup();
    renderPanel();
    await user.type(screen.getByTestId("new-vertex-label"), "car");
    await user.click(screen.getByTestId("mutation-tab-edge"));
    expect(screen.getByTestId("mutation-form-edge")).toBeInTheDocument();
    expect(screen.queryByTestId("mutation-form-vertex")).not.toBeInTheDocument();
    await user.click(screen.getByTestId("mutation-tab-vertex"));
    expect(screen.getByTestId("new-vertex-label")).toHaveValue("car");
  });
});

describe("create vertex", () => {
  it("sends the full VertexSpecification: label, ISO creation date, typed properties", async () => {
    const user = userEvent.setup();
    renderPanel();
    await user.type(screen.getByTestId("new-vertex-label"), "car");
    await user.type(screen.getByTestId("new-vertex-date"), "1970-01-01T00:02:00Z");

    await user.click(screen.getByTestId("mv-add-property"));
    await user.type(screen.getByTestId("mv-prop-id"), "wheels");
    const [row] = screen.getAllByTestId(/mv-prop-\d+-value/);
    await user.selectOptions(screen.getByLabelText("value type"), "System.Int32");
    await user.type(row, "4");

    await user.click(screen.getByTestId("create-vertex"));
    await waitFor(() => expect(createVertexMock).toHaveBeenCalledTimes(1));
    expect(createVertexMock.mock.calls[0][1]).toEqual({
      label: "car",
      creationDate: 120,
      properties: [
        { propertyId: "wheels", propertyValue: "4", fullQualifiedTypeName: "System.Int32" },
      ],
    });
    expect(screen.getByTestId("mutation-message").textContent).toContain("Vertex created");
  });

  it("empty label and date send the minimal spec (creationDate 0, no properties)", async () => {
    const user = userEvent.setup();
    renderPanel();
    await user.click(screen.getByTestId("create-vertex"));
    await waitFor(() => expect(createVertexMock).toHaveBeenCalledTimes(1));
    expect(createVertexMock.mock.calls[0][1]).toEqual({
      label: undefined,
      creationDate: 0,
      properties: undefined,
    });
  });

  it("accepts unix seconds verbatim", async () => {
    const user = userEvent.setup();
    renderPanel();
    await user.type(screen.getByTestId("new-vertex-date"), "1713862800");
    await user.click(screen.getByTestId("create-vertex"));
    await waitFor(() => expect(createVertexMock).toHaveBeenCalledTimes(1));
    expect(createVertexMock.mock.calls[0][1].creationDate).toBe(1713862800);
  });

  it("blocks submit on an invalid creation date and shows the error", async () => {
    const user = userEvent.setup();
    renderPanel();
    await user.type(screen.getByTestId("new-vertex-date"), "not a date");
    expect(screen.getByTestId("create-vertex")).toBeDisabled();
    expect(screen.getByText(/Expected Unix seconds or an ISO date\/time/)).toBeInTheDocument();
  });

  it("blocks submit on duplicate property ids", async () => {
    const user = userEvent.setup();
    renderPanel();
    await user.click(screen.getByTestId("mv-add-property"));
    await user.click(screen.getByTestId("mv-add-property"));
    const ids = screen.getAllByTestId("mv-prop-id");
    await user.type(ids[0], "age");
    await user.type(ids[1], "age");
    expect(screen.getByTestId("create-vertex")).toBeDisabled();
    expect(screen.getByText("Duplicate property id 'age'.")).toBeInTheDocument();
    expect(createVertexMock).not.toHaveBeenCalled();
  });

  it("blocks submit while a property row has an empty id or invalid value", async () => {
    const user = userEvent.setup();
    renderPanel();
    await user.click(screen.getByTestId("mv-add-property"));
    expect(screen.getByTestId("create-vertex")).toBeDisabled();
    await user.type(screen.getByTestId("mv-prop-id"), "age");
    expect(screen.getByTestId("create-vertex")).toBeEnabled();
    await user.selectOptions(screen.getByLabelText("value type"), "System.Int32");
    expect(screen.getByTestId("create-vertex")).toBeDisabled();
  });

  it("a removed property row no longer blocks or travels", async () => {
    const user = userEvent.setup();
    renderPanel();
    await user.click(screen.getByTestId("mv-add-property"));
    await user.click(screen.getByRole("button", { name: "remove property row" }));
    await user.click(screen.getByTestId("create-vertex"));
    await waitFor(() => expect(createVertexMock).toHaveBeenCalledTimes(1));
    expect(createVertexMock.mock.calls[0][1].properties).toBeUndefined();
  });
});

describe("create edge", () => {
  it("sends the full EdgeSpecification including endpoints, label, date, properties", async () => {
    const user = userEvent.setup();
    renderPanel();
    await user.click(screen.getByTestId("mutation-tab-edge"));
    await user.type(screen.getByLabelText(/source vertex id/i), "1");
    await user.type(screen.getByLabelText(/target vertex id/i), "2");
    await user.type(screen.getByLabelText(/edge property id/i), "knows");
    await user.type(screen.getByLabelText(/label \(optional\)/i), "friendship");
    await user.type(screen.getByLabelText(/creation date/i), "60");
    await user.click(screen.getByTestId("me-add-property"));
    await user.type(screen.getByTestId("me-prop-id"), "since");
    await user.type(screen.getByTestId(/me-prop-\d+-value/), "2020");

    await user.click(screen.getByTestId("create-edge"));
    await waitFor(() => expect(createEdgeMock).toHaveBeenCalledTimes(1));
    expect(createEdgeMock.mock.calls[0][1]).toEqual({
      sourceVertex: 1,
      targetVertex: 2,
      edgePropertyId: "knows",
      label: "friendship",
      creationDate: 60,
      properties: [
        { propertyId: "since", propertyValue: "2020", fullQualifiedTypeName: "System.String" },
      ],
    });
  });

  it("requires integer endpoints and an edge property id", async () => {
    const user = userEvent.setup();
    renderPanel();
    await user.click(screen.getByTestId("mutation-tab-edge"));
    expect(screen.getByTestId("create-edge")).toBeDisabled();
    await user.type(screen.getByLabelText(/source vertex id/i), "1");
    await user.type(screen.getByLabelText(/target vertex id/i), "2.5");
    await user.type(screen.getByLabelText(/edge property id/i), "knows");
    expect(screen.getByTestId("create-edge")).toBeDisabled();
  });
});

describe("property tab", () => {
  it("set sends the typed PropertySpecification to the element", async () => {
    const user = userEvent.setup();
    renderPanel();
    await user.click(screen.getByTestId("mutation-tab-property"));
    await user.type(screen.getByLabelText(/element id/i), "7");
    await user.type(screen.getByLabelText(/property id/i), "age");
    await user.selectOptions(screen.getByLabelText("value type"), "System.Int32");
    await user.type(screen.getByTestId("mp-value"), "39");
    await user.click(screen.getByRole("button", { name: "Set property" }));
    await waitFor(() => expect(setPropertyMock).toHaveBeenCalledTimes(1));
    expect(setPropertyMock).toHaveBeenCalledWith(expect.anything(), 7, "age", {
      propertyId: "age",
      propertyValue: "39",
      fullQualifiedTypeName: "System.Int32",
    });
  });

  it("an invalid typed value blocks Set but not Remove", async () => {
    const user = userEvent.setup();
    renderPanel();
    await user.click(screen.getByTestId("mutation-tab-property"));
    await user.type(screen.getByLabelText(/element id/i), "7");
    await user.type(screen.getByLabelText(/property id/i), "age");
    await user.selectOptions(screen.getByLabelText("value type"), "System.Int32");
    await user.type(screen.getByTestId("mp-value"), "not a number");
    expect(screen.getByRole("button", { name: "Set property" })).toBeDisabled();
    expect(screen.getByRole("button", { name: "Remove property" })).toBeEnabled();
    await user.click(screen.getByRole("button", { name: "Remove property" }));
    await waitFor(() => expect(removePropertyMock).toHaveBeenCalledWith(expect.anything(), 7, "age"));
  });
});

describe("remove tab", () => {
  it("removes by element id", async () => {
    const user = userEvent.setup();
    renderPanel();
    await user.click(screen.getByTestId("mutation-tab-remove"));
    await user.type(screen.getByLabelText(/element id/i), "13");
    await user.click(screen.getByRole("button", { name: "Remove element" }));
    await waitFor(() => expect(removeElementMock).toHaveBeenCalledWith(expect.anything(), 13));
    expect(screen.getByTestId("mutation-message").textContent).toContain("#13 removed");
  });
});
