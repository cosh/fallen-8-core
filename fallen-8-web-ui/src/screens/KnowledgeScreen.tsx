// MIT License
//
// KnowledgeScreen.tsx
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

import { useRef, useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useInstanceStore } from "../instances/registry";
import { describeEndpoint } from "../instances/types";
import {
  deleteDocument,
  ensureDocumentBinding,
  getDocument,
  getDocumentBinding,
  getVertex,
  ingestFile,
  ingestText,
  listDocuments,
  listEntities,
  searchDocuments,
} from "../api/endpoints";
import type {
  ChunkHit,
  DocumentBindingRole,
  DocumentSearchResult,
  DocumentSummary,
  VertexREST,
} from "../api/types";
import { useStatus } from "../state/status";
import { ErrorBox } from "../components/ErrorBox";
import { ConfirmDialog } from "../components/ConfirmDialog";
import { FileDropzone } from "../components/FileDropzone";
import { ListCapNote } from "../components/ListCapNote";
import { SCROLL_ROWS, capList, scrollRows } from "../lib/listCaps";

/**
 * Knowledge (feature semantic-layer, evolving unstructured-ingestion): the semantic layer over
 * the graph. Documents in, graph out - uploads become Document/Chunk vertices with embedded
 * text, an NLP sidecar folds them into a deduplicated Entity network, and fused search finds
 * chunks by describing them (a hit is a vertex - send it to the canvas and traverse). The layer
 * creates no index implicitly: the State panel reports the index binding and is the one place
 * that creates the required indices; ingestion is refused until the layer is bound. Uploads run
 * off-thread on a global queue - a row appears `processing` and flips live via the change feed.
 */

/** The spec's per-chunk resident estimate (text + vector + index slab), for the budget line. */
const CHUNK_BYTES_ESTIMATE = 26 * 1024;

function formatMiB(bytes: number): string {
  return `${(bytes / (1024 * 1024)).toFixed(bytes >= 100 * 1024 * 1024 ? 0 : 1)} MiB`;
}

function statusTone(status: DocumentSummary["status"]): string {
  if (status === "failed") return "text-warn";
  if (status === "processing") return "text-accent";
  return "text-fg-dim";
}

export function KnowledgeScreen() {
  const { instance, store } = useInstanceStore();
  const mergeIntoCanvas = store((s) => s.mergeIntoCanvas);
  const queryClient = useQueryClient();
  const status = useStatus(instance);
  const ingestion = status.data?.ingestion;
  const providerOn = status.data?.embedding?.enabled === true;
  const doclingReachable = ingestion?.docling?.reachable === true;

  // ---- ingest state ----
  const fileRef = useRef<HTMLInputElement>(null);
  const [textName, setTextName] = useState("");
  const [textBody, setTextBody] = useState("");
  const [textFormat, setTextFormat] = useState<"markdown" | "plain">("markdown");
  const [message, setMessage] = useState<string | null>(null);
  const [selected, setSelected] = useState<number | null>(null);
  const [confirmingDelete, setConfirmingDelete] = useState<DocumentSummary | null>(null);

  // ---- search state ----
  const [query, setQuery] = useState("");
  const [mode, setMode] = useState<"fused" | "dense" | "lexical">("fused");
  const [k, setK] = useState(10);
  const [window, setWindow] = useState(0);
  const [result, setResult] = useState<DocumentSearchResult | null>(null);

  // ---- entities state ----
  const [entityType, setEntityType] = useState("");

  const enabled = ingestion?.enabled === true;

  const binding = useQuery({
    queryKey: [instance.id, "binding"],
    queryFn: ({ signal }) => getDocumentBinding(instance, signal),
    enabled,
  });
  const bound = binding.data?.ready === true;

  const list = useQuery({
    queryKey: [instance.id, "documents"],
    queryFn: ({ signal }) => listDocuments(instance, signal),
    enabled,
  });

  const entities = useQuery({
    queryKey: [instance.id, "entities", entityType],
    queryFn: ({ signal }) => listEntities(instance, { type: entityType || undefined }, signal),
    enabled,
  });

  const detail = useQuery({
    queryKey: [instance.id, "documents", selected],
    queryFn: ({ signal }) => getDocument(instance, selected!, signal),
    enabled: enabled && selected !== null,
  });

  const invalidate = () =>
    void queryClient.invalidateQueries({ queryKey: [instance.id, "documents"] });

  const summarize = (summary: DocumentSummary | null) => {
    if (!summary) return "Ingested.";
    if (summary.status === "processing") {
      return `Queued “${summary.name}” - it is converting and indexing off-thread; the row flips to indexed when done.`;
    }
    return `Ingested “${summary.name}”: ${summary.chunkCount} chunk${summary.chunkCount === 1 ? "" : "s"}${summary.embedded ? "" : " (not embedded)"}.`;
  };

  const bind = useMutation({
    mutationFn: () => ensureDocumentBinding(instance),
    onSuccess: (b) => {
      queryClient.setQueryData([instance.id, "binding"], b);
      void queryClient.invalidateQueries({ queryKey: [instance.id, "status"] });
      setMessage(
        b?.ready
          ? "The semantic layer is bound: the required indices exist and ingestion is open."
          : "Bind ran, but an index id is taken by a wrong-shape index (see the State panel).",
      );
    },
  });

  const uploadFile = useMutation({
    mutationFn: (file: File) => ingestFile(instance, file, { embed: providerOn }),
    onSuccess: (summary) => {
      setMessage(summarize(summary));
      if (fileRef.current) fileRef.current.value = "";
      invalidate();
    },
  });

  const submitText = useMutation({
    mutationFn: () =>
      ingestText(instance, {
        name: textName,
        text: textBody,
        format: textFormat,
        embed: providerOn,
      }),
    onSuccess: (summary) => {
      setMessage(summarize(summary));
      setTextName("");
      setTextBody("");
      invalidate();
    },
  });

  const remove = useMutation({
    mutationFn: (id: number) => deleteDocument(instance, id),
    onSuccess: () => {
      setMessage("Document deleted (chunks and edges cascade).");
      setSelected(null);
      invalidate();
    },
  });

  const search = useMutation({
    mutationFn: () =>
      searchDocuments(instance, {
        queryText: query,
        mode,
        k,
        window: window > 0 ? window : undefined,
      }),
    onSuccess: (r) => setResult(r),
  });

  const sendHitsToCanvas = useMutation({
    mutationFn: async (hits: ChunkHit[]) => {
      const vertices = await Promise.all(hits.map((hit) => getVertex(instance, hit.chunkId)));
      return vertices.filter((v): v is VertexREST => v !== null);
    },
    onSuccess: (vertices) => {
      mergeIntoCanvas(vertices, []);
      setMessage(
        `${vertices.length} chunk vertex/vertices sent to the canvas - expand neighbors or start a path there.`,
      );
    },
  });

  const sendEntityToCanvas = useMutation({
    mutationFn: (id: number) => getVertex(instance, id),
    onSuccess: (vertex) => {
      if (vertex) {
        mergeIntoCanvas([vertex], []);
        setMessage("Entity sent to the canvas - expand its mentions to reach the chunks it appears in.");
      }
    },
  });

  // ---- gate: capability off (stated, with the switch to flip) ----
  if (status.data && !enabled) {
    return (
      <div className="mx-auto max-w-5xl space-y-4">
        <section className="panel">
          <h2 className="panel-title">Knowledge</h2>
          <p className="text-fg-dim p-3 text-[12px]">
            Unstructured ingestion is off on this instance
            (<code>Fallen8:Ingestion:Enabled</code>). In the compose environment it is on by
            default; <code>F8_INGESTION=false</code> turns it off together with the docling and
            NLP sidecars.
          </p>
        </section>
      </div>
    );
  }

  const documents = list.data?.documents ?? [];
  const { shown, total } = capList(documents);
  const chunkCount = list.data?.namespaceChunkCount ?? 0;
  const ceiling = list.data?.chunkCeiling ?? 0;
  const acceptedFormats = ingestion
    ? [...ingestion.textFormats, ...(doclingReachable ? ingestion.binaryFormats : [])]
    : [];

  const flatHits: ChunkHit[] = result?.hits ?? result?.documents?.flatMap((g) => g.chunks) ?? [];

  const entityRows = capList(entities.data?.entities ?? []);
  const entityTotal = entities.data?.total ?? 0;

  const roleLine = (role: DocumentBindingRole | undefined, label: string) => {
    if (!role) return null;
    const tone = role.ready ? "text-fg-dim" : role.required ? "text-warn" : "text-fg-faint";
    const state = role.ready ? "ready" : role.exists ? "conflict" : role.required ? "missing" : "not needed";
    return (
      <li className={`text-[11px] ${tone}`} data-testid={`role-${role.role}`}>
        <span className="text-fg-dim">{label}</span> <code>{role.indexId}</code> - {state}
        {role.detail ? ` (${role.detail})` : ""}
      </li>
    );
  };

  return (
    <div className="mx-auto max-w-5xl space-y-4">
      {/* ---- state: the index binding (feature semantic-layer FR-7) ---- */}
      <section className="panel" data-testid="state-panel">
        <h2 className="panel-title">State</h2>
        <div className="space-y-2 p-3">
          {binding.error && <ErrorBox error={binding.error} />}
          {binding.data && (
            <>
              <p className={`text-[12px] ${bound ? "text-fg-dim" : "text-warn"}`} data-testid="binding-state">
                {bound
                  ? "The semantic layer is bound: the indices below exist and ingestion is open."
                  : "The semantic layer is not bound yet. Nothing is created automatically - create the required indices, or point the configured ids at indices you already have."}
              </p>
              <ul className="space-y-1">
                {roleLine(binding.data.vector, "vector")}
                {roleLine(binding.data.fulltext, "fulltext")}
                {roleLine(binding.data.entity, "entity")}
              </ul>
              {!bound && (
                <div className="flex items-center gap-2">
                  <button
                    className="btn btn-accent"
                    disabled={bind.isPending}
                    onClick={() => bind.mutate()}
                    data-testid="bind-create"
                  >
                    {bind.isPending ? "Creating…" : "Create the required indexes"}
                  </button>
                  <span className="text-fg-faint text-[11px]">
                    or create them yourself on the Indexes screen with these ids.
                  </span>
                </div>
              )}
              {bind.error && <ErrorBox error={bind.error} />}
            </>
          )}
        </div>
      </section>

      {/* ---- ingest ---- */}
      <section className="panel">
        <h2 className="panel-title">Ingest</h2>
        {!bound && (
          <p className="text-warn px-3 pt-3 text-[11px]" data-testid="bind-gate-note">
            Bind the semantic layer in the State panel above before ingesting - uploads are
            refused until the required indices exist.
          </p>
        )}
        <FileDropzone
          className="m-3"
          disabled={!bound}
          onFiles={(files) => uploadFile.mutate(files[0])}
        >
          Drop a file here to ingest it ({acceptedFormats.map((f) => `.${f}`).join(" ")})
        </FileDropzone>
        <div className="flex flex-wrap items-end gap-2 px-3">
          <label className="grow">
            <span className="text-fg-faint text-[11px]">or pick a file</span>
            <input
              ref={fileRef}
              type="file"
              className="input w-full"
              accept={acceptedFormats.map((f) => `.${f}`).join(",")}
              data-testid="document-file"
            />
          </label>
          <button
            className="btn btn-accent"
            disabled={uploadFile.isPending || !bound}
            onClick={() => {
              const file = fileRef.current?.files?.[0];
              if (file) uploadFile.mutate(file);
            }}
          >
            {uploadFile.isPending ? "Ingesting…" : "Upload"}
          </button>
        </div>
        {ingestion && !doclingReachable && (
          <p className="text-fg-faint px-3 pt-2 text-[11px]">
            The docling sidecar is not reachable - only{" "}
            {ingestion.textFormats.map((f) => `.${f}`).join("/")} ingest right now; binary
            formats (pdf/docx/xlsx/pptx/html) need it.
          </p>
        )}
        <div className="flex flex-wrap items-end gap-2 px-3 pt-3 pb-3">
          <label>
            <span className="text-fg-faint text-[11px]">Name</span>
            <input
              className="input w-48"
              value={textName}
              onChange={(e) => setTextName(e.target.value)}
              placeholder="notes.md"
              data-testid="text-name"
            />
          </label>
          <label className="grow">
            <span className="text-fg-faint text-[11px]">Text (markdown chunks by headings)</span>
            <textarea
              className="input w-full font-mono"
              rows={3}
              value={textBody}
              onChange={(e) => setTextBody(e.target.value)}
              data-testid="text-body"
            />
          </label>
          <label>
            <span className="text-fg-faint text-[11px]">Format</span>
            <select
              className="input w-28"
              value={textFormat}
              onChange={(e) => setTextFormat(e.target.value as "markdown" | "plain")}
            >
              <option value="markdown">markdown</option>
              <option value="plain">plain</option>
            </select>
          </label>
          <button
            className="btn btn-accent"
            disabled={submitText.isPending || !bound || !textName.trim() || !textBody.trim()}
            onClick={() => submitText.mutate()}
            data-testid="ingest-text"
          >
            {submitText.isPending ? "Ingesting…" : "Ingest text"}
          </button>
        </div>
        <p className="text-fg-faint px-3 pb-3 text-[11px]">
          {providerOn
            ? "Chunks are embedded with the instance's provider and land in the bound vector index."
            : "The embedding provider is off - documents ingest text-only (exact-token search still works; semantic search needs the provider)."}
        </p>
        {message && <p className="text-accent text-[12px] px-3 pb-3">{message}</p>}
        {(uploadFile.error ?? submitText.error ?? remove.error) && (
          <div className="px-3 pb-3">
            <ErrorBox error={uploadFile.error ?? submitText.error ?? remove.error} />
          </div>
        )}
      </section>

      {/* ---- documents ---- */}
      <section className="panel">
        <h2 className="panel-title">Documents</h2>
        {list.data && (
          <p className="text-fg-dim p-3 text-[12px]" data-testid="chunk-budget">
            {chunkCount} of {ceiling} chunks used (~{formatMiB(chunkCount * CHUNK_BYTES_ESTIMATE)}{" "}
            resident, rough estimate). The ceiling rejects further ingestion instead of growing
            toward OOM (Fallen8:Ingestion:MaxChunksPerNamespace).
          </p>
        )}
        {list.error && (
          <div className="px-3 pb-3">
            <ErrorBox error={list.error} />
          </div>
        )}
        {shown.length === 0 ? (
          <p className="text-fg-faint p-3 text-[12px]">
            Nothing ingested yet. Drop a file or paste text above; documents become vertices you
            can search and traverse.
          </p>
        ) : (
          <div className="scroll-list" style={scrollRows(SCROLL_ROWS.documents)}>
            <table className="w-full text-[12px]">
              <thead>
                <tr>
                  <th className="table-cell">Name</th>
                  <th className="table-cell">Format</th>
                  <th className="table-cell">Status</th>
                  <th className="table-cell text-right">Chunks</th>
                  <th className="table-cell text-right">Pages</th>
                  <th className="table-cell">Embedding</th>
                  <th className="table-cell text-right"></th>
                </tr>
              </thead>
              <tbody>
                {shown.map((doc) => (
                  <tr
                    key={doc.documentId}
                    onClick={() => setSelected(doc.documentId === selected ? null : doc.documentId)}
                    style={{ cursor: "pointer" }}
                    data-testid={`document-row-${doc.documentId}`}
                  >
                    <td className="table-cell font-semibold">{doc.name}</td>
                    <td className="table-cell text-fg-dim">{doc.sourceFormat}</td>
                    <td className={`table-cell ${statusTone(doc.status)}`}>
                      {doc.status}
                      {doc.status === "failed" && doc.error ? ` - ${doc.error}` : ""}
                    </td>
                    <td className="table-cell text-right">{doc.chunkCount}</td>
                    <td className="table-cell text-right">{doc.pageCount ?? ""}</td>
                    <td className="table-cell text-fg-dim">
                      {doc.embedded ? (doc.embeddingModelStale ? "stale model" : "embedded") : "text-only"}
                    </td>
                    <td className="table-cell text-right">
                      <button
                        className="btn btn-danger"
                        onClick={(e) => {
                          e.stopPropagation();
                          setConfirmingDelete(doc);
                        }}
                        data-testid={`delete-${doc.documentId}`}
                      >
                        Delete
                      </button>
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
        <ListCapNote shown={shown.length} total={total} />
        {detail.data && selected !== null && (
          <div className="space-y-3 p-3" data-testid="document-detail">
            <p className="text-fg-dim text-[12px]">
              {detail.data.summary.name}: {detail.data.chunks.length} chunk
              {detail.data.chunks.length === 1 ? "" : "s"} (previews; the full text is the chunk
              vertex&apos;s <code>text</code> property).
            </p>
            <div className="scroll-list" style={scrollRows(SCROLL_ROWS.default)}>
              <table className="w-full text-[12px]">
                <tbody>
                  {detail.data.chunks.map((chunk) => (
                    <tr key={chunk.chunkId}>
                      <td className="table-cell text-fg-dim">#{chunk.order}</td>
                      <td className="table-cell text-fg-dim">{chunk.kind}</td>
                      <td className="table-cell text-fg-dim">{chunk.headingPath ?? ""}</td>
                      <td className="table-cell">{chunk.textPreview}</td>
                      <td className="table-cell text-fg-faint">
                        {(chunk.identifiers ?? []).join(" ")}
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          </div>
        )}
      </section>

      {/* ---- entities (feature semantic-layer FR-6) ---- */}
      <section className="panel" data-testid="entities">
        <h2 className="panel-title">Entities</h2>
        <div className="flex flex-wrap items-end gap-2 p-3">
          <label>
            <span className="text-fg-faint text-[11px]">Type filter (PERSON/ORG/GPE/…)</span>
            <input
              className="input w-40"
              value={entityType}
              onChange={(e) => setEntityType(e.target.value)}
              placeholder="all types"
              data-testid="entity-type"
            />
          </label>
          <span className="text-fg-dim text-[12px]">
            {entityRows.shown.length} of {entityTotal} entit{entityTotal === 1 ? "y" : "ies"} (most
            mentioned first)
          </span>
        </div>
        {entities.error && (
          <div className="px-3 pb-3">
            <ErrorBox error={entities.error} />
          </div>
        )}
        {entityRows.shown.length === 0 ? (
          <p className="text-fg-faint p-3 text-[12px]">
            No entities yet. When the NLP sidecar is on, ingested chunks are enriched into a
            deduplicated Entity network you can filter and traverse from.
          </p>
        ) : (
          <div className="scroll-list" style={scrollRows(SCROLL_ROWS.default)}>
            <table className="w-full text-[12px]">
              <thead>
                <tr>
                  <th className="table-cell">Entity</th>
                  <th className="table-cell">Type</th>
                  <th className="table-cell text-right">Mentions</th>
                  <th className="table-cell text-right"></th>
                </tr>
              </thead>
              <tbody>
                {entityRows.shown.map((entity) => (
                  <tr key={entity.id} data-testid={`entity-row-${entity.id}`}>
                    <td className="table-cell font-semibold">{entity.text}</td>
                    <td className="table-cell text-fg-dim">{entity.type}</td>
                    <td className="table-cell text-right">{entity.mentionCount}</td>
                    <td className="table-cell text-right">
                      <button
                        className="btn"
                        disabled={sendEntityToCanvas.isPending}
                        onClick={() => sendEntityToCanvas.mutate(entity.id)}
                        data-testid={`entity-canvas-${entity.id}`}
                      >
                        Canvas
                      </button>
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
        <ListCapNote shown={entityRows.shown.length} total={entityRows.total} />
      </section>

      {/* ---- search ---- */}
      <section className="panel">
        <h2 className="panel-title">Search chunks</h2>
        <div className="flex flex-wrap items-end gap-2 p-3">
          <label className="grow">
            <span className="text-fg-faint text-[11px]">Describe what you are looking for</span>
            <input
              className="input w-full"
              value={query}
              onChange={(e) => setQuery(e.target.value)}
              placeholder="the server that terminates tls for the shop"
              data-testid="search-query"
            />
          </label>
          <label>
            <span className="text-fg-faint text-[11px]">Mode</span>
            <select className="input w-28" value={mode} onChange={(e) => setMode(e.target.value as typeof mode)}>
              <option value="fused">fused</option>
              <option value="dense">dense</option>
              <option value="lexical">lexical</option>
            </select>
          </label>
          <label>
            <span className="text-fg-faint text-[11px]">k</span>
            <input
              className="input w-24"
              type="number"
              min={1}
              max={100}
              value={k}
              onChange={(e) => setK(Number(e.target.value) || 10)}
            />
          </label>
          <label>
            <span className="text-fg-faint text-[11px]">Window</span>
            <input
              className="input w-24"
              type="number"
              min={0}
              max={5}
              value={window}
              onChange={(e) => setWindow(Number(e.target.value) || 0)}
            />
          </label>
          <button
            className="btn btn-accent"
            disabled={search.isPending || !query.trim()}
            onClick={() => search.mutate()}
            data-testid="search-run"
          >
            {search.isPending ? "Searching…" : "Search"}
          </button>
        </div>
        {search.error && (
          <div className="px-3 pb-3">
            <ErrorBox error={search.error} />
          </div>
        )}
        {result && (
          <div className="space-y-3 p-3" data-testid="search-results">
            <div className="flex flex-wrap items-end gap-2">
              <span className="text-fg-dim text-[12px]">
                {flatHits.length} hit{flatHits.length === 1 ? "" : "s"} via {result.modeUsed}
                {result.modeUsed !== mode && mode === "fused"
                  ? " (degraded: one retrieval side is unavailable)"
                  : ""}
              </span>
              <button
                className="btn"
                disabled={flatHits.length === 0 || sendHitsToCanvas.isPending}
                onClick={() => sendHitsToCanvas.mutate(flatHits)}
                data-testid="send-hits-to-canvas"
              >
                Send hits to canvas
              </button>
            </div>
            <div className="scroll-list" style={scrollRows(SCROLL_ROWS.default)}>
              <table className="w-full text-[12px]">
                <tbody>
                  {flatHits.map((hit) => (
                    <tr key={hit.chunkId}>
                      <td className="table-cell text-fg-dim">#{hit.chunkId}</td>
                      <td className="table-cell text-fg-dim text-right">{hit.score.toFixed(4)}</td>
                      <td className="table-cell text-fg-dim">{hit.headingPath ?? ""}</td>
                      <td className="table-cell">
                        {hit.text.length > 240 ? `${hit.text.slice(0, 240)}…` : hit.text}
                      </td>
                      <td className="table-cell text-right">
                        {/* Per-hit inspect: send THIS chunk vertex to the canvas, where the
                            standard affordances (inspect, expand neighbors, use as a path seed)
                            apply. A chunk is an ordinary vertex, so nothing special is needed. */}
                        <button
                          className="btn"
                          disabled={sendHitsToCanvas.isPending}
                          onClick={() => sendHitsToCanvas.mutate([hit])}
                          data-testid={`inspect-hit-${hit.chunkId}`}
                        >
                          Inspect
                        </button>
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
            <p className="text-fg-faint px-1 text-[11px]">
              A hit is an ordinary chunk vertex. Send it (or all hits) to the canvas to inspect
              it, expand its neighbours, or start a path or subgraph from it.
            </p>
          </div>
        )}
      </section>

      <ConfirmDialog
        open={confirmingDelete !== null}
        title="Delete document"
        description={
          confirmingDelete
            ? `Removes “${confirmingDelete.name}”, its ${confirmingDelete.chunkCount} chunk(s) and every edge on them (including mentions and hand-drawn ones). This cannot be undone.`
            : ""
        }
        instanceName={instance.name}
        endpoint={describeEndpoint(instance)}
        confirmLabel="Delete"
        onConfirm={() => {
          if (confirmingDelete) remove.mutate(confirmingDelete.documentId);
          setConfirmingDelete(null);
        }}
        onCancel={() => setConfirmingDelete(null)}
      />
    </div>
  );
}
