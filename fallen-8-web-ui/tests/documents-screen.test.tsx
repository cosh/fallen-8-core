// MIT License
//
// documents-screen.test.tsx
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
  DocumentList,
  DocumentSearchResult,
  DocumentSearchSpecification,
  DocumentSummary,
  IngestTextSpecification,
  StatusREST,
  VertexREST,
} from "../src/api/types";
import { resetInstanceStoresForTests } from "../src/state/instanceStore";

/**
 * Documents screen (feature unstructured-ingestion FR-9): the capability gate, the honest
 * degraded modes (provider off, docling unreachable), the chunk budget, the ingest flows,
 * the delete confirm, and fused search with the modeUsed degrade note.
 */

const getStatusMock = vi.fn<(i: InstanceConfig) => Promise<StatusREST | null>>();
const listDocumentsMock = vi.fn<(i: InstanceConfig) => Promise<DocumentList | null>>();
const ingestTextMock =
  vi.fn<(i: InstanceConfig, spec: IngestTextSpecification) => Promise<DocumentSummary | null>>();
const searchDocumentsMock =
  vi.fn<(i: InstanceConfig, spec: DocumentSearchSpecification) => Promise<DocumentSearchResult | null>>();
const deleteDocumentMock = vi.fn<(i: InstanceConfig, id: number) => Promise<void>>();
const getVertexMock = vi.fn<(i: InstanceConfig, id: number) => Promise<VertexREST | null>>();

vi.mock("../src/api/endpoints", async (importOriginal) => {
  const original = await importOriginal<typeof import("../src/api/endpoints")>();
  return {
    ...original,
    getStatus: (i: InstanceConfig) => getStatusMock(i),
    listDocuments: (i: InstanceConfig) => listDocumentsMock(i),
    ingestText: (i: InstanceConfig, spec: IngestTextSpecification) => ingestTextMock(i, spec),
    searchDocuments: (i: InstanceConfig, spec: DocumentSearchSpecification) =>
      searchDocumentsMock(i, spec),
    deleteDocument: (i: InstanceConfig, id: number) => deleteDocumentMock(i, id),
    getVertex: (i: InstanceConfig, id: number) => getVertexMock(i, id),
  };
});

import { DocumentsScreen } from "../src/screens/DocumentsScreen";

function status(options: {
  ingestionEnabled?: boolean;
  providerOn?: boolean;
  doclingReachable?: boolean;
}): StatusREST {
  return {
    vertexCount: 0,
    edgeCount: 0,
    usedMemory: 0,
    availableIndexPlugins: [],
    availablePathPlugins: [],
    availableAnalyticsPlugins: [],
    availableServicePlugins: [],
    embedding: {
      enabled: options.providerOn ?? true,
      backend: "Onnx",
      modelName: "bge-micro-v2",
      modelVersion: "",
      dimension: 4,
      intendedMetric: "Cosine",
      loaded: true,
    },
    ingestion: {
      enabled: options.ingestionEnabled ?? true,
      textFormats: ["txt", "md"],
      binaryFormats: ["pdf", "docx", "xlsx", "pptx", "html"],
      docling: {
        configured: options.doclingReachable ?? true,
        reachable: options.doclingReachable ?? true,
      },
      limits: {
        maxUploadBytes: 33554432,
        maxPages: 500,
        maxChunksPerDocument: 2000,
        maxChunksPerNamespace: 100000,
        maxLinksPerChunk: 16,
      },
      embeddingName: "default",
      vectorIndexId: "documents",
      fulltextIndexId: "documents-text",
    },
  };
}

const DOC: DocumentSummary = {
  documentId: 7,
  name: "edge-notes.md",
  sourceFormat: "md",
  status: "indexed",
  chunkCount: 3,
  contentHash: "abc",
  converter: "none",
  embedded: true,
  embeddingModelStale: false,
};

function documentList(documents: DocumentSummary[], chunks = 3): DocumentList {
  return {
    documents,
    namespaceChunkCount: chunks,
    chunkCeiling: 100000,
    currentEmbeddingModel: "bge-micro-v2#4#Cosine",
  };
}

function renderScreen() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={client}>
      <DocumentsScreen />
    </QueryClientProvider>,
  );
}

beforeEach(() => {
  resetInstanceStoresForTests();
  localStorage.clear();
  getStatusMock.mockReset().mockResolvedValue(status({}));
  listDocumentsMock.mockReset().mockResolvedValue(documentList([DOC]));
  ingestTextMock.mockReset().mockResolvedValue({ ...DOC, linksCreated: 0 });
  searchDocumentsMock.mockReset().mockResolvedValue({ modeUsed: "fused", hits: [] });
  deleteDocumentMock.mockReset().mockResolvedValue(undefined);
  getVertexMock
    .mockReset()
    .mockResolvedValue({ id: 42, creationDate: "", modificationDate: "", label: "Chunk" });
});

describe("capability gating", () => {
  it("states the off switch when ingestion is disabled and fires no list query", async () => {
    getStatusMock.mockResolvedValue(status({ ingestionEnabled: false }));
    renderScreen();

    await waitFor(() =>
      expect(screen.getByText(/Unstructured ingestion is off/i)).toBeInTheDocument(),
    );
    expect(listDocumentsMock).not.toHaveBeenCalled();
  });
});

describe("documents list and budget", () => {
  it("renders the chunk budget and the document rows", async () => {
    renderScreen();

    await waitFor(() => expect(screen.getByTestId("chunk-budget")).toBeInTheDocument());
    expect(screen.getByTestId("chunk-budget").textContent).toContain("3 of 100000 chunks");
    expect(screen.getByTestId("document-row-7")).toHaveTextContent("edge-notes.md");
    expect(screen.getByTestId("document-row-7")).toHaveTextContent("indexed");
    expect(screen.getByTestId("document-row-7")).toHaveTextContent("embedded");
  });

  it("shows a failed document's error and the stale-model badge", async () => {
    listDocumentsMock.mockResolvedValue(
      documentList([
        { ...DOC, documentId: 8, status: "failed", error: "sidecar melted", embedded: false },
        { ...DOC, documentId: 9, embeddingModelStale: true },
      ]),
    );
    renderScreen();

    await waitFor(() => expect(screen.getByTestId("document-row-8")).toBeInTheDocument());
    expect(screen.getByTestId("document-row-8")).toHaveTextContent("failed - sidecar melted");
    expect(screen.getByTestId("document-row-9")).toHaveTextContent("stale model");
  });

  it("opens the typed-name confirm before deleting", async () => {
    const user = userEvent.setup();
    renderScreen();

    await waitFor(() => expect(screen.getByTestId("delete-7")).toBeInTheDocument());
    await user.click(screen.getByTestId("delete-7"));

    expect(await screen.findByText(/Removes “edge-notes\.md”/)).toBeInTheDocument();
    expect(deleteDocumentMock).not.toHaveBeenCalled();
  });
});

describe("degraded modes", () => {
  it("says text-only when the embedding provider is off and ingests with embed=false", async () => {
    getStatusMock.mockResolvedValue(status({ providerOn: false }));
    const user = userEvent.setup();
    renderScreen();

    await waitFor(() =>
      expect(screen.getByText(/embedding provider is off/i)).toBeInTheDocument(),
    );

    await user.type(screen.getByTestId("text-name"), "notes.md");
    await user.type(screen.getByTestId("text-body"), "# H hello");
    await user.click(screen.getByTestId("ingest-text"));

    await waitFor(() => expect(ingestTextMock).toHaveBeenCalledTimes(1));
    expect(ingestTextMock.mock.calls[0][1].embed).toBe(false);
  });

  it("limits the file picker to text formats while docling is unreachable", async () => {
    getStatusMock.mockResolvedValue(status({ doclingReachable: false }));
    renderScreen();

    await waitFor(() =>
      expect(screen.getByText(/docling sidecar is not reachable/i)).toBeInTheDocument(),
    );
    expect(screen.getByTestId("document-file")).toHaveAttribute("accept", ".txt,.md");
  });
});

describe("ingest text", () => {
  it("sends the markdown ingest and reports the outcome", async () => {
    const user = userEvent.setup();
    renderScreen();

    await waitFor(() => expect(screen.getByTestId("ingest-text")).toBeInTheDocument());
    await user.type(screen.getByTestId("text-name"), "edge-notes.md");
    await user.type(screen.getByTestId("text-body"), "# Edge content");
    await user.click(screen.getByTestId("ingest-text"));

    await waitFor(() => expect(ingestTextMock).toHaveBeenCalledTimes(1));
    const spec = ingestTextMock.mock.calls[0][1];
    expect(spec.name).toBe("edge-notes.md");
    expect(spec.format).toBe("markdown");
    expect(spec.embed).toBe(true);
    expect(await screen.findByText(/Ingested “edge-notes\.md”: 3 chunks/)).toBeInTheDocument();
  });
});

describe("fused search", () => {
  it("runs the search, renders hits, and sends them to the canvas", async () => {
    searchDocumentsMock.mockResolvedValue({
      modeUsed: "fused",
      hits: [
        {
          chunkId: 42,
          documentId: 7,
          score: 0.0321,
          order: 1,
          text: "The EDGE_TLS_01 box terminates tls.",
          headingPath: "Edge",
        },
      ],
    });
    const user = userEvent.setup();
    renderScreen();

    await waitFor(() => expect(screen.getByTestId("search-query")).toBeInTheDocument());
    await user.type(screen.getByTestId("search-query"), "tls terminator");
    await user.click(screen.getByTestId("search-run"));

    await waitFor(() => expect(screen.getByTestId("search-results")).toBeInTheDocument());
    expect(searchDocumentsMock.mock.calls[0][1]).toMatchObject({
      queryText: "tls terminator",
      mode: "fused",
      k: 10,
    });
    expect(screen.getByText(/1 hit via fused/)).toBeInTheDocument();
    expect(screen.getByText(/EDGE_TLS_01/)).toBeInTheDocument();

    await user.click(screen.getByTestId("send-hits-to-canvas"));
    await waitFor(() => expect(getVertexMock).toHaveBeenCalledWith(expect.anything(), 42));
    expect(await screen.findByText(/sent to the canvas/)).toBeInTheDocument();
  });

  it("labels an honest degrade when fused ran lexical-only", async () => {
    searchDocumentsMock.mockResolvedValue({ modeUsed: "lexical", hits: [] });
    const user = userEvent.setup();
    renderScreen();

    await waitFor(() => expect(screen.getByTestId("search-query")).toBeInTheDocument());
    await user.type(screen.getByTestId("search-query"), "anything");
    await user.click(screen.getByTestId("search-run"));

    await waitFor(() =>
      expect(screen.getByText(/via lexical \(degraded/)).toBeInTheDocument(),
    );
  });
});
