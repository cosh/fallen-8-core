// MIT License
//
// knowledge-screen.test.tsx
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
import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import type { InstanceConfig } from "../src/instances/types";
import type {
  DocumentBinding,
  DocumentEntityList,
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
 * Knowledge screen (feature semantic-layer): the State panel binding gate (ingest is refused
 * until the layer is bound, and the one create path is explicit), the drag-and-drop dropzone,
 * the Entities view, plus the inherited unstructured-ingestion behaviour (capability gate,
 * degraded modes, chunk budget, delete confirm, fused search) and the async processing message.
 */

const getStatusMock = vi.fn<(i: InstanceConfig) => Promise<StatusREST | null>>();
const listDocumentsMock = vi.fn<(i: InstanceConfig) => Promise<DocumentList | null>>();
const ingestTextMock =
  vi.fn<(i: InstanceConfig, spec: IngestTextSpecification) => Promise<DocumentSummary | null>>();
const ingestFileMock =
  vi.fn<(i: InstanceConfig, file: File, opts: unknown) => Promise<DocumentSummary | null>>();
const searchDocumentsMock =
  vi.fn<(i: InstanceConfig, spec: DocumentSearchSpecification) => Promise<DocumentSearchResult | null>>();
const deleteDocumentMock = vi.fn<(i: InstanceConfig, id: number) => Promise<void>>();
const getVertexMock = vi.fn<(i: InstanceConfig, id: number) => Promise<VertexREST | null>>();
const getBindingMock = vi.fn<(i: InstanceConfig) => Promise<DocumentBinding | null>>();
const ensureBindingMock = vi.fn<(i: InstanceConfig) => Promise<DocumentBinding | null>>();
const listEntitiesMock =
  vi.fn<(i: InstanceConfig, o: { type?: string }) => Promise<DocumentEntityList | null>>();

vi.mock("../src/api/endpoints", async (importOriginal) => {
  const original = await importOriginal<typeof import("../src/api/endpoints")>();
  return {
    ...original,
    getStatus: (i: InstanceConfig) => getStatusMock(i),
    listDocuments: (i: InstanceConfig) => listDocumentsMock(i),
    ingestText: (i: InstanceConfig, spec: IngestTextSpecification) => ingestTextMock(i, spec),
    ingestFile: (i: InstanceConfig, file: File, opts: unknown) => ingestFileMock(i, file, opts),
    searchDocuments: (i: InstanceConfig, spec: DocumentSearchSpecification) =>
      searchDocumentsMock(i, spec),
    deleteDocument: (i: InstanceConfig, id: number) => deleteDocumentMock(i, id),
    getVertex: (i: InstanceConfig, id: number) => getVertexMock(i, id),
    getDocumentBinding: (i: InstanceConfig) => getBindingMock(i),
    ensureDocumentBinding: (i: InstanceConfig) => ensureBindingMock(i),
    listEntities: (i: InstanceConfig, o: { type?: string }) => listEntitiesMock(i, o),
  };
});

import { KnowledgeScreen } from "../src/screens/KnowledgeScreen";

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

function role(name: "vector" | "fulltext" | "entity", indexId: string, ready: boolean): DocumentBinding["vector"] {
  return { role: name, indexId, required: true, exists: ready, ready, detail: ready ? undefined : "is absent" };
}

function binding(ready: boolean): DocumentBinding {
  return {
    ready,
    vector: role("vector", "documents", ready),
    fulltext: role("fulltext", "documents-text", ready),
    entity: { role: "entity", indexId: "documents-entities", required: false, exists: ready, ready },
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
      <KnowledgeScreen />
    </QueryClientProvider>,
  );
}

beforeEach(() => {
  resetInstanceStoresForTests();
  localStorage.clear();
  getStatusMock.mockReset().mockResolvedValue(status({}));
  listDocumentsMock.mockReset().mockResolvedValue(documentList([DOC]));
  ingestTextMock.mockReset().mockResolvedValue(DOC);
  ingestFileMock.mockReset().mockResolvedValue({ ...DOC, status: "processing", chunkCount: 0 });
  searchDocumentsMock.mockReset().mockResolvedValue({ modeUsed: "fused", hits: [] });
  deleteDocumentMock.mockReset().mockResolvedValue(undefined);
  getVertexMock
    .mockReset()
    .mockResolvedValue({ id: 42, creationDate: "", modificationDate: "", label: "Chunk" });
  getBindingMock.mockReset().mockResolvedValue(binding(true));
  ensureBindingMock.mockReset().mockResolvedValue(binding(true));
  listEntitiesMock.mockReset().mockResolvedValue({ entities: [], total: 0 });
});

describe("capability gating", () => {
  it("states the off switch when ingestion is disabled and fires no queries", async () => {
    getStatusMock.mockResolvedValue(status({ ingestionEnabled: false }));
    renderScreen();

    await waitFor(() =>
      expect(screen.getByText(/Unstructured ingestion is off/i)).toBeInTheDocument(),
    );
    expect(listDocumentsMock).not.toHaveBeenCalled();
    expect(getBindingMock).not.toHaveBeenCalled();
  });
});

describe("State panel / binding gate (FR-7)", () => {
  it("refuses ingestion and offers the create action until the layer is bound", async () => {
    getBindingMock.mockResolvedValue(binding(false));
    renderScreen();

    await waitFor(() => expect(screen.getByTestId("binding-state")).toBeInTheDocument());
    expect(screen.getByTestId("bind-gate-note")).toBeInTheDocument();
    // Ingest controls are disabled while unbound.
    expect(screen.getByTestId("ingest-text")).toBeDisabled();
    expect(screen.getByTestId("role-vector")).toHaveTextContent("documents");
  });

  it("creates the required indices via the explicit bind action", async () => {
    getBindingMock.mockResolvedValue(binding(false));
    const user = userEvent.setup();
    renderScreen();

    await waitFor(() => expect(screen.getByTestId("bind-create")).toBeInTheDocument());
    await user.click(screen.getByTestId("bind-create"));

    await waitFor(() => expect(ensureBindingMock).toHaveBeenCalledTimes(1));
    // The panel flips to ready (the ensure result is written into the binding query).
    await waitFor(() => expect(screen.getByTestId("binding-state")).toHaveTextContent(/bound/i));
  });

  it("does not create indices on its own - no ensure call on a normal load", async () => {
    renderScreen();
    await waitFor(() => expect(getBindingMock).toHaveBeenCalled());
    expect(ensureBindingMock).not.toHaveBeenCalled();
  });
});

describe("drag-and-drop ingest", () => {
  it("ingests a file dropped on the dropzone", async () => {
    renderScreen();

    ingestFileMock.mockResolvedValue({ ...DOC, name: "dropped.md", status: "processing", chunkCount: 0 });
    // The dropzone renders immediately, but a drop is a no-op until the layer is bound; wait for
    // the bound state (the unbound gate note is gone) before dropping.
    await waitFor(() => expect(screen.queryByTestId("bind-gate-note")).not.toBeInTheDocument());
    const file = new File(["# H\n\nbody"], "dropped.md", { type: "text/markdown" });
    fireEvent.drop(screen.getByTestId("dropzone"), { dataTransfer: { files: [file] } });

    await waitFor(() => expect(ingestFileMock).toHaveBeenCalledTimes(1));
    expect(ingestFileMock.mock.calls[0][1].name).toBe("dropped.md");
    // Async accept: the processing stub yields the "queued" message.
    expect(await screen.findByText(/Queued “dropped\.md”/)).toBeInTheDocument();
  });

  it("ignores a drop while the layer is unbound", async () => {
    getBindingMock.mockResolvedValue(binding(false));
    renderScreen();

    await waitFor(() => expect(screen.getByTestId("dropzone")).toBeInTheDocument());
    const file = new File(["x"], "nope.md", { type: "text/markdown" });
    fireEvent.drop(screen.getByTestId("dropzone"), { dataTransfer: { files: [file] } });

    // No ingest happens; the drop is a no-op until bound.
    await new Promise((r) => setTimeout(r, 20));
    expect(ingestFileMock).not.toHaveBeenCalled();
  });
});

describe("entities view (FR-6)", () => {
  it("renders the entity network ranked by mention count", async () => {
    listEntitiesMock.mockResolvedValue({
      entities: [
        { id: 100, text: "Muster GmbH", type: "ORG", mentionCount: 5 },
        { id: 101, text: "München", type: "LOC", mentionCount: 2 },
      ],
      total: 2,
    });
    renderScreen();

    await waitFor(() => expect(screen.getByTestId("entity-row-100")).toBeInTheDocument());
    expect(screen.getByTestId("entity-row-100")).toHaveTextContent("Muster GmbH");
    expect(screen.getByTestId("entity-row-100")).toHaveTextContent("ORG");
    expect(screen.getByTestId("entity-row-100")).toHaveTextContent("5");
    expect(screen.getByTestId("entity-row-101")).toHaveTextContent("München");
  });

  it("filters entities by type", async () => {
    const user = userEvent.setup();
    renderScreen();

    await waitFor(() => expect(screen.getByTestId("entity-type")).toBeInTheDocument());
    await user.type(screen.getByTestId("entity-type"), "ORG");

    await waitFor(() =>
      expect(listEntitiesMock).toHaveBeenCalledWith(expect.anything(), { type: "ORG" }),
    );
  });

  it("hints the label set the shipped NLP models actually emit (B17)", async () => {
    renderScreen();

    // The type filter is an exact, case-insensitive compare against the RAW spaCy label the
    // sidecar stores verbatim (fallen-8-nlp/app/enrich.py label=ent.label_), and the shipped
    // en_core_web_lg / en_core_web_trf emit the OntoNotes set. A hint naming PER, which no
    // shipped model emits, can only ever send somebody to a filter that matches nothing.
    await waitFor(() => expect(screen.getByTestId("entity-type")).toBeInTheDocument());
    const hint = screen.getByText(/Type filter/);
    expect(hint.textContent).toContain("PERSON");
    expect(hint.textContent).toContain("GPE");
    expect(hint.textContent).not.toMatch(/PER/);
    expect(hint.textContent).not.toMatch(/LOC/);
  });

  it("sends an entity to the canvas", async () => {
    listEntitiesMock.mockResolvedValue({
      entities: [{ id: 100, text: "Muster GmbH", type: "ORG", mentionCount: 5 }],
      total: 1,
    });
    getVertexMock.mockResolvedValue({ id: 100, creationDate: "", modificationDate: "", label: "Entity" });
    const user = userEvent.setup();
    renderScreen();

    await waitFor(() => expect(screen.getByTestId("entity-canvas-100")).toBeInTheDocument());
    await user.click(screen.getByTestId("entity-canvas-100"));

    await waitFor(() => expect(getVertexMock).toHaveBeenCalledWith(expect.anything(), 100));
    expect(await screen.findByText(/Entity sent to the canvas/)).toBeInTheDocument();
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

  it("reports the async 'queued' message when the ingest is accepted as processing", async () => {
    ingestTextMock.mockResolvedValue({ ...DOC, name: "big.md", status: "processing", chunkCount: 0 });
    const user = userEvent.setup();
    renderScreen();

    await waitFor(() => expect(screen.getByTestId("ingest-text")).toBeInTheDocument());
    await user.type(screen.getByTestId("text-name"), "big.md");
    await user.type(screen.getByTestId("text-body"), "# Big content");
    await user.click(screen.getByTestId("ingest-text"));

    expect(await screen.findByText(/Queued “big\.md”/)).toBeInTheDocument();
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
