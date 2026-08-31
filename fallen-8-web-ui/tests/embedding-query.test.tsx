// MIT License
//
// embedding-query.test.tsx
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
  EmbeddingSearchSpecification,
  PluginSpecification,
  StatusREST,
  VectorSearchResultREST,
} from "../src/api/types";
import { getInstanceStore, resetInstanceStoresForTests } from "../src/state/instanceStore";

/**
 * Embedding semantics across the split screens (features element-embeddings /
 * studio-semantics / index-workspace): bound-index create options and the bound badge +
 * content guard on the Indexes screen; semantic search by text on the Query screen.
 */

const getStatusMock = vi.fn<(i: InstanceConfig) => Promise<StatusREST | null>>();
const createIndexMock = vi.fn<(i: InstanceConfig, spec: PluginSpecification) => Promise<boolean | null>>();
const embeddingSearchMock =
  vi.fn<(i: InstanceConfig, spec: EmbeddingSearchSpecification) => Promise<VectorSearchResultREST | null>>();

vi.mock("../src/api/endpoints", async (importOriginal) => {
  const original = await importOriginal<typeof import("../src/api/endpoints")>();
  return {
    ...original,
    getStatus: (i: InstanceConfig) => getStatusMock(i),
    createIndex: (i: InstanceConfig, spec: PluginSpecification) => createIndexMock(i, spec),
    embeddingSearch: (i: InstanceConfig, spec: EmbeddingSearchSpecification) =>
      embeddingSearchMock(i, spec),
  };
});

import { QueryScreen } from "../src/screens/QueryScreen";
import { IndexesScreen } from "../src/screens/IndexesScreen";

const STATUS: StatusREST = {
  vertexCount: 0,
  edgeCount: 0,
  usedMemory: 0,
  indices: [
    { indexId: "raw", pluginType: "VectorIndex", capabilities: ["vector"] },
    {
      indexId: "emb",
      pluginType: "VectorIndex",
      embeddingName: "default",
      model: null,
      capabilities: ["vector"],
    },
  ],
  availableIndexPlugins: ["DictionaryIndex", "VectorIndex"],
  availablePathPlugins: [],
  availableAnalyticsPlugins: [],
  availableServicePlugins: [],
};

// Provider state rides /status (feature embedding-out-of-box), same mock as the inventory.
function statusWithProvider(enabled: boolean): StatusREST {
  return {
    ...STATUS,
    embedding: {
      enabled,
      backend: "Onnx",
      modelName: "bge-micro-v2",
      modelVersion: "",
      dimension: 4,
      intendedMetric: "Cosine",
      loaded: true,
    },
  };
}

function renderScreen(
  Screen: typeof QueryScreen | typeof IndexesScreen,
  providerEnabled?: boolean,
) {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  if (providerEnabled !== undefined) {
    getStatusMock.mockResolvedValue(statusWithProvider(providerEnabled));
  }
  return render(
    <QueryClientProvider client={client}>
      <Screen />
    </QueryClientProvider>,
  );
}

beforeEach(() => {
  resetInstanceStoresForTests();
  localStorage.clear();
  getStatusMock.mockReset().mockResolvedValue(STATUS);
  createIndexMock.mockReset().mockResolvedValue(true);
  embeddingSearchMock.mockReset().mockResolvedValue({
    metric: "Cosine",
    higherIsBetter: true,
    results: [],
  });
});

describe("bound vector index (Indexes screen)", () => {
  it("shows a bound badge in the inventory", async () => {
    renderScreen(IndexesScreen);
    await waitFor(() => expect(screen.getByTestId("index-bound-emb")).toBeInTheDocument());
    expect(screen.getByTestId("index-bound-emb")).toHaveTextContent("bound:default");
  });

  it("create sends embeddingName/model as typed literals when set", async () => {
    const user = userEvent.setup();
    renderScreen(IndexesScreen);
    await waitFor(() => expect(screen.getByTestId("index-type").tagName).toBe("SELECT"));
    await user.selectOptions(screen.getByTestId("index-type"), "VectorIndex");
    await user.type(screen.getByLabelText(/index id/i), "bound2");
    await user.type(screen.getByTestId("vector-embedding-name"), "default");
    await user.click(screen.getByRole("button", { name: "Create" }));

    await waitFor(() => expect(createIndexMock).toHaveBeenCalledTimes(1));
    const options = createIndexMock.mock.calls[0][1].pluginOptions!;
    expect(options.embeddingName).toEqual({
      propertyId: "embeddingName",
      propertyValue: "default",
      fullQualifiedTypeName: "System.String",
    });
    expect(options.dimension.propertyValue).toBe("384");
    expect(options.model).toBeUndefined();
  });

  it("offers no content forms against a bound index, with the reason", async () => {
    const user = userEvent.setup();
    renderScreen(IndexesScreen);
    await waitFor(() => expect(screen.getByTestId("index-row-emb")).toBeInTheDocument());
    await user.click(screen.getByTestId("index-row-emb"));

    await waitFor(() =>
      expect(screen.getByTestId("bound-content-note")).toBeInTheDocument(),
    );
    expect(screen.queryByTestId("vector-add")).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Add vector" })).not.toBeInTheDocument();
  });
});

describe("semantic search (Query screen)", () => {
  it("is offered as its own query mode, without first picking an index", async () => {
    // The whole point of feature semantic-search-onramp: text-in kNN used to be a toggle inside
    // the index mode's vector form, so the one capability that needs no knowledge of the data
    // model was reachable only by first knowing the index model.
    renderScreen(QueryScreen, true);
    const mode = await screen.findByTestId("query-mode");
    expect(
      [...mode.querySelectorAll("option")].map((o) => (o as HTMLOptionElement).value),
    ).toEqual(["property", "index", "semantic"]);
  });

  it("sends the typed text, k, kind and label to /embedding/search", async () => {
    const user = userEvent.setup();
    renderScreen(QueryScreen, true);
    await user.selectOptions(screen.getByTestId("query-mode"), "semantic");
    await waitFor(() =>
      expect(screen.getByTestId("semantic-index-select")).toBeInTheDocument(),
    );
    await user.selectOptions(screen.getByTestId("semantic-index-select"), "emb");
    await user.type(screen.getByTestId("vector-search-text"), "red bicycles");
    await user.selectOptions(screen.getByLabelText(/element kind/i), "vertex");
    await user.type(screen.getByLabelText(/label constraint/i), "movie");
    await user.click(screen.getByTestId("scan-run"));

    await waitFor(() => expect(embeddingSearchMock).toHaveBeenCalledTimes(1));
    expect(embeddingSearchMock.mock.calls[0][1]).toEqual({
      indexId: "emb",
      text: "red bicycles",
      k: 10,
      kind: "vertex",
      label: "movie",
    });
    // The caption names which backend and embedding function turned the text into a vector.
    expect(screen.getByTestId("vector-search-provenance")).toHaveTextContent(
      "via Onnx · bge-micro-v2",
    );
  });

  it("offers only vector indices, naming the embedding a bound one projects", async () => {
    const user = userEvent.setup();
    getStatusMock.mockResolvedValue({
      ...statusWithProvider(true),
      indices: [
        { indexId: "by-name", pluginType: "DictionaryIndex", capabilities: ["equality"] },
        ...STATUS.indices!,
      ],
    });
    renderScreen(QueryScreen);
    await user.selectOptions(screen.getByTestId("query-mode"), "semantic");

    const select = await screen.findByTestId("semantic-index-select");
    const options = [...select.querySelectorAll("option")].map((o) => o.textContent);
    // A dictionary index cannot rank vectors, so offering it would only produce a 400.
    expect(options).not.toContain("by-name");
    expect(options).toContain("emb (bound:default)");
    expect(options).toContain("raw");
  });

  it("preselects the only vector index there is, and needs no picking", async () => {
    const user = userEvent.setup();
    getStatusMock.mockResolvedValue({
      ...statusWithProvider(true),
      indices: [
        {
          indexId: "solo",
          pluginType: "VectorIndex",
          embeddingName: "default",
          capabilities: ["vector"],
        },
      ],
    });
    renderScreen(QueryScreen);
    await user.selectOptions(screen.getByTestId("query-mode"), "semantic");
    await waitFor(() =>
      expect(screen.getByTestId("semantic-index-select")).toHaveValue("solo"),
    );

    await user.type(screen.getByTestId("vector-search-text"), "anything");
    await user.click(screen.getByTestId("scan-run"));
    await waitFor(() => expect(embeddingSearchMock).toHaveBeenCalledTimes(1));
    expect(embeddingSearchMock.mock.calls[0][1].indexId).toBe("solo");
  });

  it("keeps its index apart from the index mode's, in both directions", async () => {
    // The two modes choose from different sets, so they hold different fields. One shared field
    // meant picking a vector index here silently replaced the operator's index-mode selection
    // AND the query form that went with it.
    const user = userEvent.setup();
    getStatusMock.mockResolvedValue({
      ...statusWithProvider(true),
      indices: [
        { indexId: "words", pluginType: "RegExIndex", capabilities: ["equality", "fulltext"] },
        { indexId: "v1", pluginType: "VectorIndex", capabilities: ["vector"] },
        { indexId: "v2", pluginType: "VectorIndex", capabilities: ["vector"] },
      ],
    });
    renderScreen(QueryScreen);
    await user.selectOptions(screen.getByTestId("query-mode"), "index");
    await waitFor(() => expect(screen.getByTestId("index-select")).toBeInTheDocument());
    await user.selectOptions(screen.getByTestId("index-select"), "words");
    await waitFor(() => expect(screen.getByTestId("form-fulltext")).toBeInTheDocument());
    await user.click(screen.getByTestId("form-fulltext"));

    // The non-vector index is not inherited: nothing is picked until the operator picks.
    await user.selectOptions(screen.getByTestId("query-mode"), "semantic");
    await waitFor(() => expect(screen.getByTestId("semantic-index-select")).toHaveValue(""));
    await user.type(screen.getByTestId("vector-search-text"), "anything");
    expect(screen.getByTestId("scan-run")).toBeDisabled();
    await user.selectOptions(screen.getByTestId("semantic-index-select"), "v2");
    expect(screen.getByTestId("scan-run")).toBeEnabled();

    // ...and picking here left the index mode exactly as it was, form included: the fulltext
    // form's own query input is still the one on screen.
    await user.selectOptions(screen.getByTestId("query-mode"), "index");
    await waitFor(() => expect(screen.getByTestId("index-select")).toHaveValue("words"));
    expect(screen.getByLabelText("query")).toBeInTheDocument();
    expect(screen.queryByTestId("vector-query")).not.toBeInTheDocument();
  });

  it("drops a stored pick that no longer names a vector index", async () => {
    // The index was deleted since the draft was written, or the draft came from an instance that
    // had it. Sending it would spend a round trip to be told what the inventory already says.
    localStorage.setItem(
      "f8.workspace.local",
      JSON.stringify({
        state: { queryDraft: { mode: "semantic", semanticIndexId: "long-gone" } },
        version: 0,
      }),
    );
    getStatusMock.mockResolvedValue({
      ...statusWithProvider(true),
      indices: [{ indexId: "raw", pluginType: "VectorIndex", capabilities: ["vector"] }],
    });
    renderScreen(QueryScreen);

    // One vector index is left, so it falls back to the sole answer rather than an absent id.
    await waitFor(() => expect(screen.getByTestId("semantic-index-select")).toHaveValue("raw"));
  });

  it("asks for a pick when the stored one is gone and there is more than one left", async () => {
    localStorage.setItem(
      "f8.workspace.local",
      JSON.stringify({
        state: { queryDraft: { mode: "semantic", semanticIndexId: "long-gone" } },
        version: 0,
      }),
    );
    renderScreen(QueryScreen, true); // STATUS carries two vector indices
    await waitFor(() => expect(screen.getByTestId("semantic-index-select")).toHaveValue(""));
    expect(screen.getByTestId("scan-run")).toBeDisabled();
  });

  it("disables the query text and says why when the provider is off", async () => {
    const user = userEvent.setup();
    renderScreen(QueryScreen, false);
    await user.selectOptions(screen.getByTestId("query-mode"), "semantic");

    await waitFor(() => expect(screen.getByTestId("vector-search-text")).toBeDisabled());
    expect(screen.getByTestId("semantic-provider-off")).toHaveTextContent(
      /embedding provider is off/,
    );
    expect(screen.getByTestId("scan-run")).toBeDisabled();
  });

  it("distinguishes an unreported provider from one that is off", async () => {
    const user = userEvent.setup();
    getStatusMock.mockResolvedValue(STATUS); // no `embedding` block at all
    renderScreen(QueryScreen);
    await user.selectOptions(screen.getByTestId("query-mode"), "semantic");

    await waitFor(() =>
      expect(screen.getByTestId("semantic-provider-off")).toHaveTextContent(
        /does not report an embedding provider/,
      ),
    );
  });

  // The mode is PERSISTED per instance, so a semantic search survives the operator turning the
  // provider off afterwards: the text field is still the one on screen and Run query is disabled,
  // and the caption must not go on naming a backend and an embedding function for a request that
  // cannot be made. Its sibling caption in SemanticQueryEditor is gated the same way.
  it("stops naming a backend when the provider goes off under a persisted semantic mode", async () => {
    const user = userEvent.setup();
    const first = renderScreen(QueryScreen, true);
    await user.selectOptions(screen.getByTestId("query-mode"), "semantic");
    await waitFor(() =>
      expect(screen.getByTestId("vector-search-provenance")).toHaveTextContent("via Onnx"),
    );
    first.unmount();

    renderScreen(QueryScreen, false);
    await waitFor(() => expect(screen.getByTestId("vector-search-text")).toBeInTheDocument());
    await waitFor(() =>
      expect(screen.getByTestId("vector-search-provenance")).not.toHaveTextContent("via"),
    );
    expect(screen.getByTestId("scan-run")).toBeDisabled();
  });

  it("leaves a find-similar exclusion behind in the vector form it belongs to", async () => {
    // The exclusion answers "elements like THIS element's vector". Following the operator into a
    // text search it dropped a hit, spent one of their k on the over-fetch, and explained itself
    // with a chip about a vector the typed query never had.
    const user = userEvent.setup();
    // The real find-similar gesture: it hands over the source element's own vector and its id.
    getInstanceStore("local").getState().setScanPrefill({
      indexId: "emb",
      vectorText: "[1, 0, 0, 0]",
      sourceElementId: 42,
      label: "movie",
      kind: "vertex",
    });
    renderScreen(QueryScreen, true);
    await waitFor(() => expect(screen.getByTestId("exclude-source-chip")).toBeInTheDocument());

    await user.selectOptions(screen.getByTestId("query-mode"), "semantic");
    expect(screen.queryByTestId("exclude-source-chip")).not.toBeInTheDocument();
    // The prefill's index does not leak across modes either, so this mode wants its own pick.
    await user.selectOptions(screen.getByTestId("semantic-index-select"), "emb");
    await user.type(screen.getByTestId("vector-search-text"), "anything");
    await user.click(screen.getByTestId("scan-run"));

    // No over-fetch, so no hit is silently dropped from a search that never had a source element.
    await waitFor(() => expect(embeddingSearchMock).toHaveBeenCalledTimes(1));
    expect(embeddingSearchMock.mock.calls[0][1].k).toBe(10);

    // ...and going back to the vector form finds the exclusion intact, where it belongs.
    await user.selectOptions(screen.getByTestId("query-mode"), "index");
    await waitFor(() => expect(screen.getByTestId("exclude-source-chip")).toBeInTheDocument());
  });

  it("will not run a kNN with a k the engine would refuse", async () => {
    const user = userEvent.setup();
    renderScreen(QueryScreen, true);
    await user.selectOptions(screen.getByTestId("query-mode"), "semantic");
    await waitFor(() => expect(screen.getByTestId("semantic-index-select")).toBeInTheDocument());
    await user.selectOptions(screen.getByTestId("semantic-index-select"), "emb");
    await user.type(screen.getByTestId("vector-search-text"), "red bicycles");
    expect(screen.getByTestId("scan-run")).toBeEnabled();

    // An emptied box coerces to Number("") === 0, which the engine answers 400 for. min/max on
    // the input never stop an empty value.
    await user.clear(screen.getByLabelText(/^k /));
    expect(screen.getByTestId("scan-run")).toBeDisabled();
    expect(screen.getByTestId("k-invalid")).toBeInTheDocument();

    await user.type(screen.getByLabelText(/^k /), "5");
    expect(screen.getByTestId("scan-run")).toBeEnabled();
    expect(screen.queryByTestId("k-invalid")).not.toBeInTheDocument();
    expect(embeddingSearchMock).not.toHaveBeenCalled();
  });

  it("offers an index whose capabilities the server did not report, rather than hiding it", async () => {
    // indexCapabilities errs toward every family for an unknown plugin on a pre-capabilities
    // server, so the picker cannot be the absolute filter the help text once claimed.
    const user = userEvent.setup();
    getStatusMock.mockResolvedValue({
      ...statusWithProvider(true),
      indices: [{ indexId: "acme", pluginType: "AcmeIndex" }],
    });
    renderScreen(QueryScreen);
    await user.selectOptions(screen.getByTestId("query-mode"), "semantic");

    await waitFor(() => expect(screen.getByTestId("semantic-index-select")).toHaveValue("acme"));
    expect(screen.queryByTestId("semantic-onramp")).not.toBeInTheDocument();
  });

  it("no longer offers a text source inside the index mode's vector form", async () => {
    // FR-2: text-in has ONE home now. The vector form is bring-your-own-vector only, which is
    // also what the find-similar gesture prefills.
    const user = userEvent.setup();
    renderScreen(QueryScreen, true);
    await user.selectOptions(screen.getByTestId("query-mode"), "index");
    await waitFor(() => expect(screen.getByTestId("index-select")).toBeInTheDocument());
    await user.selectOptions(screen.getByTestId("index-select"), "emb");

    await waitFor(() => expect(screen.getByTestId("vector-query")).toBeInTheDocument());
    expect(screen.queryByTestId("vector-source-text")).not.toBeInTheDocument();
    expect(screen.queryByTestId("vector-source-vector")).not.toBeInTheDocument();
  });
});

describe("the on-ramp: embeddings present, no vector index to rank them", () => {
  // The state a real AUTOSAR run left behind: many embedded elements, two claim indices, and
  // no vector index - which reads as "this instance cannot search by meaning" when it is one
  // create call away from doing exactly that.
  const NO_VECTOR_INDEX: StatusREST = {
    ...STATUS,
    indices: [{ indexId: "f8i-claims", pluginType: "DictionaryIndex", capabilities: ["equality"] }],
  };

  function statusNoVectorIndex(providerEnabled: boolean): StatusREST {
    return { ...NO_VECTOR_INDEX, embedding: statusWithProvider(providerEnabled).embedding };
  }

  it("offers the create instead of an empty picker, prefilled from the provider", async () => {
    const user = userEvent.setup();
    getStatusMock.mockResolvedValue(statusNoVectorIndex(true));
    renderScreen(QueryScreen);
    await user.selectOptions(screen.getByTestId("query-mode"), "semantic");

    await waitFor(() => expect(screen.getByTestId("semantic-onramp")).toBeInTheDocument());
    expect(screen.queryByTestId("semantic-index-select")).not.toBeInTheDocument();
    // The provider's own numbers, not a guess: an index whose dimension disagrees with the model
    // writing into it is refused on every later embed and every later search.
    expect(screen.getByTestId("onramp-dimension")).toHaveValue(4);
    expect(screen.getByTestId("onramp-metric")).toHaveValue("Cosine");
    expect(screen.getByTestId("onramp-embedding-name")).toHaveValue("default");
  });

  it("creates a bound vector index and lands in the search form", async () => {
    const user = userEvent.setup();
    getStatusMock.mockResolvedValue(statusNoVectorIndex(true));
    renderScreen(QueryScreen);
    await user.selectOptions(screen.getByTestId("query-mode"), "semantic");
    await waitFor(() => expect(screen.getByTestId("semantic-onramp")).toBeInTheDocument());

    // The index exists from the create onwards, so the refetched inventory reports it.
    getStatusMock.mockResolvedValue({
      ...statusNoVectorIndex(true),
      indices: [
        ...NO_VECTOR_INDEX.indices!,
        {
          indexId: "embeddings",
          pluginType: "VectorIndex",
          embeddingName: "default",
          capabilities: ["vector"],
          values: 12,
        },
      ],
    });
    await user.click(screen.getByTestId("onramp-create"));

    await waitFor(() => expect(createIndexMock).toHaveBeenCalledTimes(1));
    const spec = createIndexMock.mock.calls[0][1];
    expect(spec.uniqueId).toBe("embeddings");
    expect(spec.pluginType).toBe("VectorIndex");
    expect(spec.pluginOptions!.dimension.propertyValue).toBe("4");
    expect(spec.pluginOptions!.embeddingName).toEqual({
      propertyId: "embeddingName",
      propertyValue: "default",
      fullQualifiedTypeName: "System.String",
    });

    // The user typed a query and clicked create; nothing else should be required of them.
    await waitFor(() =>
      expect(screen.getByTestId("semantic-index-select")).toHaveValue("embeddings"),
    );
    expect(screen.queryByTestId("semantic-onramp")).not.toBeInTheDocument();
  });

  it("selects the index it made, even if another appeared in the meantime", async () => {
    // Leaving this to the single-index preselect only works while exactly one vector index
    // exists. A second one arriving between the create and the refetch (another session) would
    // otherwise leave the picker asking the operator to choose the thing they just created.
    const user = userEvent.setup();
    getStatusMock.mockResolvedValue(statusNoVectorIndex(true));
    renderScreen(QueryScreen);
    await user.selectOptions(screen.getByTestId("query-mode"), "semantic");
    await waitFor(() => expect(screen.getByTestId("semantic-onramp")).toBeInTheDocument());

    getStatusMock.mockResolvedValue({
      ...statusNoVectorIndex(true),
      indices: [
        ...NO_VECTOR_INDEX.indices!,
        { indexId: "aaa-someone-else", pluginType: "VectorIndex", capabilities: ["vector"], values: 1 },
        {
          indexId: "embeddings",
          pluginType: "VectorIndex",
          embeddingName: "default",
          capabilities: ["vector"],
          values: 0,
        },
      ],
    });
    await user.click(screen.getByTestId("onramp-create"));

    await waitFor(() =>
      expect(screen.getByTestId("semantic-index-select")).toHaveValue("embeddings"),
    );
  });

  it("leaves the index mode's own selection alone when it creates one", async () => {
    // indexId is shared between the two modes, so the on-ramp deliberately does NOT write it:
    // the refreshed inventory holds exactly one vector index and the preselect finds it. Writing
    // would silently replace whatever the operator had picked under 'ask an index'.
    const user = userEvent.setup();
    getStatusMock.mockResolvedValue(statusNoVectorIndex(true));
    renderScreen(QueryScreen);
    await user.selectOptions(screen.getByTestId("query-mode"), "index");
    await waitFor(() => expect(screen.getByTestId("index-select")).toBeInTheDocument());
    await user.selectOptions(screen.getByTestId("index-select"), "f8i-claims");

    await user.selectOptions(screen.getByTestId("query-mode"), "semantic");
    await waitFor(() => expect(screen.getByTestId("semantic-onramp")).toBeInTheDocument());
    getStatusMock.mockResolvedValue({
      ...statusNoVectorIndex(true),
      indices: [
        ...NO_VECTOR_INDEX.indices!,
        { indexId: "embeddings", pluginType: "VectorIndex", embeddingName: "default", capabilities: ["vector"] },
      ],
    });
    await user.click(screen.getByTestId("onramp-create"));
    await waitFor(() =>
      expect(screen.getByTestId("semantic-index-select")).toHaveValue("embeddings"),
    );

    await user.selectOptions(screen.getByTestId("query-mode"), "index");
    await waitFor(() => expect(screen.getByTestId("index-select")).toHaveValue("f8i-claims"));
  });

  it("reports a refused create, and keeps naming the id the server actually refused", async () => {
    const user = userEvent.setup();
    getStatusMock.mockResolvedValue(statusNoVectorIndex(true));
    createIndexMock.mockResolvedValue(false);
    renderScreen(QueryScreen);
    await user.selectOptions(screen.getByTestId("query-mode"), "semantic");
    await waitFor(() => expect(screen.getByTestId("semantic-onramp")).toBeInTheDocument());
    await user.click(screen.getByTestId("onramp-create"));

    await waitFor(() => expect(screen.getByTestId("onramp-refused")).toBeInTheDocument());
    expect(screen.getByTestId("onramp-refused")).toHaveTextContent(/'embeddings' was NOT created/);
    expect(screen.getByTestId("onramp-refused")).toHaveTextContent(/may already exist/);

    // Editing the id must not rewrite history: the message named a real refusal of a real id.
    await user.type(screen.getByTestId("onramp-index-id"), "-2");
    expect(screen.getByTestId("onramp-refused")).toHaveTextContent(/'embeddings' was NOT created/);
  });

  it("attributes only the provider's OWN numbers to the provider", async () => {
    // The sentence used to interpolate the editable fields, so a hand-typed dimension came back
    // as "prefilled from this instance's embedding provider ... at 128 dimensions".
    const user = userEvent.setup();
    getStatusMock.mockResolvedValue(statusNoVectorIndex(true));
    renderScreen(QueryScreen);
    await user.selectOptions(screen.getByTestId("query-mode"), "semantic");
    await waitFor(() => expect(screen.getByTestId("semantic-onramp")).toBeInTheDocument());

    await user.clear(screen.getByTestId("onramp-dimension"));
    await user.type(screen.getByTestId("onramp-dimension"), "128");
    await user.selectOptions(screen.getByTestId("onramp-metric"), "L2");

    const note = screen.getByTestId("onramp-provider-note");
    expect(note).toHaveTextContent(/at 4 dimensions, Cosine/);
    expect(note).not.toHaveTextContent(/128/);
    expect(note).not.toHaveTextContent(/L2/);
  });

  it("says the numbers are defaults when an enabled provider names no dimension", async () => {
    const user = userEvent.setup();
    getStatusMock.mockResolvedValue({
      ...statusNoVectorIndex(true),
      embedding: { ...statusWithProvider(true).embedding!, dimension: 0 },
    });
    renderScreen(QueryScreen);
    await user.selectOptions(screen.getByTestId("query-mode"), "semantic");

    await waitFor(() =>
      expect(screen.getByTestId("onramp-provider-unnamed")).toBeInTheDocument(),
    );
    expect(screen.queryByTestId("onramp-provider-note")).not.toBeInTheDocument();
    expect(screen.getByTestId("onramp-dimension")).toHaveValue(384);
  });

  it("refuses to submit a dimension the engine cannot take", async () => {
    const user = userEvent.setup();
    getStatusMock.mockResolvedValue(statusNoVectorIndex(true));
    renderScreen(QueryScreen);
    await user.selectOptions(screen.getByTestId("query-mode"), "semantic");
    await waitFor(() => expect(screen.getByTestId("semantic-onramp")).toBeInTheDocument());

    await user.clear(screen.getByTestId("onramp-dimension"));
    expect(screen.getByTestId("onramp-create")).toBeDisabled();
    expect(screen.getByTestId("onramp-dimension-invalid")).toBeInTheDocument();

    await user.type(screen.getByTestId("onramp-dimension"), "5000");
    expect(screen.getByTestId("onramp-create")).toBeDisabled();

    await user.clear(screen.getByTestId("onramp-dimension"));
    await user.type(screen.getByTestId("onramp-dimension"), "1024");
    expect(screen.getByTestId("onramp-create")).toBeEnabled();
    expect(screen.queryByTestId("onramp-dimension-invalid")).not.toBeInTheDocument();
    expect(createIndexMock).not.toHaveBeenCalled();
  });

  it("does not offer to build an index nobody could query here", async () => {
    // Without the provider the search this index exists for answers 403, so the create would
    // be a worse dead end than the one it fixes: it says what is missing and where to go.
    const user = userEvent.setup();
    getStatusMock.mockResolvedValue(statusNoVectorIndex(false));
    renderScreen(QueryScreen);
    await user.selectOptions(screen.getByTestId("query-mode"), "semantic");

    await waitFor(() =>
      expect(screen.getByTestId("semantic-onramp-provider-off")).toBeInTheDocument(),
    );
    expect(screen.queryByTestId("onramp-create")).not.toBeInTheDocument();
    expect(screen.getByTestId("semantic-onramp-provider-off")).toHaveTextContent(
      /provider is also off/,
    );
  });

  it("makes no claim while the status request is still in flight", async () => {
    // Offering to CREATE on the strength of a pending request is the same false certainty this
    // mode exists to remove, one level down: nobody knows yet whether an index is there.
    const user = userEvent.setup();
    getStatusMock.mockReturnValue(new Promise(() => {}));
    renderScreen(QueryScreen);
    await user.selectOptions(screen.getByTestId("query-mode"), "semantic");

    await waitFor(() =>
      expect(screen.getByTestId("semantic-inventory-pending")).toBeInTheDocument(),
    );
    expect(screen.queryByTestId("semantic-onramp")).not.toBeInTheDocument();
    expect(screen.queryByTestId("semantic-onramp-provider-off")).not.toBeInTheDocument();
    // Nor a verdict on the provider: an unanswered request is not a configuration choice.
    expect(screen.queryByTestId("semantic-provider-off")).not.toBeInTheDocument();
    // The picker is there but empty, and says so rather than being a blank control.
    expect(screen.getByTestId("semantic-index-select")).toHaveValue("");
  });

  it("makes no claim when the status request failed outright", async () => {
    // An unreachable or unauthorized instance knows nothing about what it holds, and a persistent
    // "this instance has none, create one" would be a lie that survives on screen.
    const user = userEvent.setup();
    getStatusMock.mockRejectedValue(new Error("connect ECONNREFUSED"));
    renderScreen(QueryScreen);
    await user.selectOptions(screen.getByTestId("query-mode"), "semantic");

    await waitFor(() =>
      expect(screen.getByTestId("semantic-inventory-pending")).toBeInTheDocument(),
    );
    expect(screen.queryByTestId("semantic-onramp")).not.toBeInTheDocument();
    expect(screen.queryByTestId("onramp-create")).not.toBeInTheDocument();
    expect(screen.queryByTestId("semantic-provider-off")).not.toBeInTheDocument();
  });

  it("never claims there is no vector index on a server that reports no inventory", async () => {
    // An older /status has no `indices` field at all. Guessing "none" would offer a create for
    // an index that may well already exist, so the honest control is a free-form id.
    const user = userEvent.setup();
    getStatusMock.mockResolvedValue({
      ...statusWithProvider(true),
      indices: undefined as unknown as StatusREST["indices"],
    });
    renderScreen(QueryScreen);
    await user.selectOptions(screen.getByTestId("query-mode"), "semantic");

    await waitFor(() => expect(screen.getByTestId("semantic-index-free")).toBeInTheDocument());
    expect(screen.queryByTestId("semantic-onramp")).not.toBeInTheDocument();
  });
});

describe("an empty vector index says so, instead of reading as 'nothing is similar'", () => {
  function statusWithCounts(values: number): StatusREST {
    return {
      ...statusWithProvider(true),
      indices: [
        {
          indexId: "emb",
          pluginType: "VectorIndex",
          embeddingName: "default",
          model: null,
          capabilities: ["vector"],
          keys: 0,
          values,
        },
      ],
    };
  }

  it("warns when the selected index has no members, and names the embedding it is bound to", async () => {
    getStatusMock.mockResolvedValue(statusWithCounts(0));
    const user = userEvent.setup();
    renderScreen(QueryScreen);
    await user.selectOptions(screen.getByTestId("query-mode"), "index");
    await waitFor(() => expect(screen.getByTestId("index-select")).toBeInTheDocument());
    await user.selectOptions(screen.getByTestId("index-select"), "emb");

    // kNN over a zero-length scan SUCCEEDS, so both search handlers answer 200 with an empty list.
    // Without this the operator reads "no similar elements" when the truth is "nothing written yet".
    const hint = await screen.findByTestId("empty-vector-index-hint");
    expect(hint).toHaveTextContent(/no members yet/);
    expect(hint).toHaveTextContent(/'default' embedding/);
  });

  it("warns in the semantic mode too, where the same empty 200 arrives", async () => {
    getStatusMock.mockResolvedValue(statusWithCounts(0));
    const user = userEvent.setup();
    renderScreen(QueryScreen);
    await user.selectOptions(screen.getByTestId("query-mode"), "semantic");

    const hint = await screen.findByTestId("empty-vector-index-hint");
    expect(hint).toHaveTextContent(/no members yet/);
    expect(hint).toHaveTextContent(/'default' embedding/);
  });

  it("stays quiet for an index that has members", async () => {
    getStatusMock.mockResolvedValue(statusWithCounts(5));
    const user = userEvent.setup();
    renderScreen(QueryScreen);
    await user.selectOptions(screen.getByTestId("query-mode"), "index");
    await waitFor(() => expect(screen.getByTestId("index-select")).toBeInTheDocument());
    await user.selectOptions(screen.getByTestId("index-select"), "emb");

    expect(screen.queryByTestId("empty-vector-index-hint")).not.toBeInTheDocument();
  });

  it("stays quiet when the server does not report a member count, rather than guessing empty", async () => {
    getStatusMock.mockResolvedValue(statusWithProvider(true));
    const user = userEvent.setup();
    renderScreen(QueryScreen);
    await user.selectOptions(screen.getByTestId("query-mode"), "index");
    await waitFor(() => expect(screen.getByTestId("index-select")).toBeInTheDocument());
    await user.selectOptions(screen.getByTestId("index-select"), "emb");

    // An absent count is "not reported", which is not the same claim as zero.
    expect(screen.queryByTestId("empty-vector-index-hint")).not.toBeInTheDocument();
  });
});
