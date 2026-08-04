// MIT License
//
// sampleLoader.ts
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

/**
 * Sample-graph loader (feature sample-graphs). Fetches a curated dataset just-in-time and
 * ingests it: (optional wipe), POST /bulk/import, create the manifest's index recipes, hand
 * the imported elements back for the canvas. No embedding work happens for baked datasets:
 * the vectors are in the fallen8-jsonl file and a bound VectorIndex projects them on import.
 *
 * A sample may additionally carry DOCUMENTS (feature knowledge-demo), which adds three steps
 * after index creation: seed the equality indexes, bind the semantic layer, then ingest the
 * documents through the real /document pipeline. All three are conditional on their manifest
 * field, so a document-free sample follows exactly the path it always did.
 */

import type { DocumentSummary, GraphREST, StatusREST, VertexREST } from "../api/types";
import type { InstanceConfig } from "../instances/types";
import {
  addToIndex,
  createIndex,
  ensureDocumentBinding,
  getDocument,
  getGraph,
  importBulk,
  ingestFile,
  ingestText,
  tabulaRasa,
} from "../api/endpoints";
import { CANVAS_ELEMENT_CAP } from "./canvasCap";
import type {
  SampleDocument,
  SampleEmbeddingInfo,
  SampleIndexSeed,
  SampleManifestEntry,
  SamplesManifest,
} from "./samples";

/**
 * Where the datasets live. Default is SAME-ORIGIN /samples: the datasets ship with the app
 * (Vite serves the repo samples/ in dev and copies it into wwwroot at build; the apiApp serves
 * it), so the gallery shows the samples the app was built with, a newly added sample appears on
 * rebuild without a GitHub round-trip, and it works offline. Override with VITE_F8_SAMPLES_BASE
 * to point at a remote mirror (e.g. GitHub raw `.../<ref>/samples`) or a fork. No trailing slash.
 */
export function samplesBaseUrl(): string {
  const override = (import.meta.env.VITE_F8_SAMPLES_BASE as string | undefined)?.trim();
  return (override || "/samples").replace(/\/$/, "");
}

export async function fetchSamplesManifest(
  baseUrl: string,
  signal?: AbortSignal,
): Promise<SamplesManifest> {
  const response = await fetch(`${baseUrl}/index.json`, { signal });
  if (!response.ok) {
    throw new Error(
      `Could not fetch the sample manifest from ${baseUrl}/index.json (${response.status}). ` +
        "Set VITE_F8_SAMPLES_BASE if the datasets live elsewhere.",
    );
  }
  return (await response.json()) as SamplesManifest;
}

export type LoadStep =
  | "wiping"
  | "fetching"
  | "importing"
  | "indexing"
  | "seeding"
  | "binding"
  | "ingesting"
  | "rendering";

/** Progress sink; `detail` names the current document or seeding count when there is one. */
export type LoadStepReporter = (step: LoadStep, detail?: string) => void;

export interface LoadResult {
  graph: GraphREST;
  verticesCreated: number;
  edgesCreated: number;
  /** Index keys written by the seeding step (0 when the sample has no `indexSeeds`). */
  tagsSeeded: number;
  /** Documents that reached `indexed` (0 when the sample has no `documents`). */
  documentsIngested: number;
  /** Chunks those documents produced. */
  chunksCreated: number;
}

/**
 * How long one document may take to reach `indexed`. Ingestion is asynchronous and drains a
 * single global FIFO queue, and a scanned PDF converting in docling is the slow case, so this
 * is generous. It exists to fail a wedged load instead of hanging the UI forever.
 */
const DOCUMENT_TIMEOUT_MS = 300_000;
const DOCUMENT_POLL_MS = 1_500;

/** Consecutive failed status reads tolerated before a document is given up on. */
const MAX_POLL_FAILURES = 3;

/**
 * A document the server reported as `failed`. Distinguished from a transport error so the poll
 * loop retries the latter and never the former: a failed ingest is a verdict, not a blip.
 */
class IngestFailure extends Error {}

/**
 * Fills the manifest's equality indexes from imported properties. See `SampleIndexSeed` for why
 * this step has to exist at all. Vertices without the property are skipped (a wind-farm
 * technician carries no asset tag by design).
 */
async function seedIndexes(
  instance: InstanceConfig,
  seeds: SampleIndexSeed[],
  expectedVertices: number,
  step: LoadStepReporter,
): Promise<number> {
  const graph = await getGraph(instance, CANVAS_ELEMENT_CAP);
  const vertices: VertexREST[] = graph?.vertices ?? [];

  // GET /graph returns at most one page and says nothing about having truncated. Seeding a
  // SUBSET is the one outcome worse than failing here: the load would report success and then
  // link only part of the corpus, which is exactly the silent failure this step exists to
  // prevent. So compare against what the import actually created.
  if (vertices.length < expectedVertices) {
    throw new Error(
      `Only ${vertices.length} of ${expectedVertices} imported vertices came back in one page ` +
        `(cap ${CANVAS_ELEMENT_CAP}), so seeding '${seeds.map((s) => s.indexId).join(", ")}' ` +
        "would cover part of the graph and linking would silently miss the rest.",
    );
  }

  // All targets up front: the progress denominator has to be the total across every seed, not
  // the current seed's share, or a second seed reports past 100%.
  const targets = seeds.flatMap((seed) =>
    vertices.flatMap((vertex) => {
      const value = vertex.properties?.find((p) => p.propertyId === seed.propertyId)?.propertyValue;
      return typeof value === "string" && value.length > 0
        ? [{ indexId: seed.indexId, id: vertex.id, value }]
        : [];
    }),
  );

  let seeded = 0;
  for (const target of targets) {
    const added = await addToIndex(instance, target.indexId, {
      graphElementId: target.id,
      key: { propertyValue: target.value, fullQualifiedTypeName: "System.String" },
    });
    // The server answers a boolean; false means the index or the element was not found.
    // Failing loudly beats a load that "succeeds" and then links nothing.
    if (!added) {
      throw new Error(
        `Could not add vertex ${target.id} ('${target.value}') to index '${target.indexId}'.`,
      );
    }
    seeded++;
    step("seeding", `${seeded}/${targets.length}`);
  }

  return seeded;
}

/**
 * Polls one document to a terminal state. Throws on `failed` and on timeout.
 *
 * Deliberately tolerant of a few consecutive read failures. By the time this runs the load has
 * already erased the previous graph, imported, seeded and possibly ingested earlier documents,
 * and it may sit here for minutes; letting a single 502 from a proxy discard all of that is the
 * wrong trade. The tolerance is deliberately small (a few seconds), not a reconnect strategy. Persistent failure still aborts, carrying the last error.
 */
async function awaitDocument(
  instance: InstanceConfig,
  documentId: number,
  name: string,
  step: LoadStepReporter,
): Promise<DocumentSummary> {
  const deadline = Date.now() + DOCUMENT_TIMEOUT_MS;
  let consecutiveFailures = 0;
  let lastError = "";

  for (;;) {
    try {
      const detail = await getDocument(instance, documentId);
      if (detail === null) {
        // A 204/empty body, not an error: the document vertex is gone. Reporting this as a
        // sidecar timeout would be a confidently wrong diagnosis.
        consecutiveFailures++;
        lastError = `document ${documentId} is no longer there (deleted while it converted?)`;
      } else {
        const summary = detail.summary;
        if (summary?.status === "indexed") return summary;
        if (summary?.status === "failed") {
          throw new IngestFailure(
            `Ingesting '${name}' failed: ${summary.error ?? "no reason given"}.`,
          );
        }
        consecutiveFailures = 0;
      }
    } catch (error) {
      if (error instanceof IngestFailure) throw error;
      consecutiveFailures++;
      lastError = error instanceof Error ? error.message : String(error);
    }

    if (consecutiveFailures >= MAX_POLL_FAILURES) {
      throw new Error(
        `Lost track of '${name}' after ${MAX_POLL_FAILURES} failed status reads (${lastError}). ` +
          "Earlier documents in this sample are already ingested; the Knowledge screen shows " +
          "what landed.",
      );
    }
    if (Date.now() > deadline) {
      throw new Error(
        `Ingesting '${name}' did not finish within ${DOCUMENT_TIMEOUT_MS / 1000}s ` +
          "(the docling or embedding sidecar may be overloaded).",
      );
    }
    step("ingesting", `${name} (converting)`);
    await new Promise((resolve) => setTimeout(resolve, DOCUMENT_POLL_MS));
  }
}

/**
 * Ingests the sample's documents through the REAL pipeline, in manifest order. Text documents
 * skip the docling sidecar entirely; binary ones convert in it. Both carry the linking
 * allowlist, which is what joins the resulting chunks to the imported asset graph.
 */
async function ingestDocuments(
  instance: InstanceConfig,
  entry: SampleManifestEntry,
  baseUrl: string,
  step: LoadStepReporter,
): Promise<{ documentsIngested: number; chunksCreated: number }> {
  const documents: SampleDocument[] = entry.documents ?? [];
  const link = entry.linkIndexIds?.length ? { indexIds: entry.linkIndexIds } : undefined;
  let chunksCreated = 0;

  for (const document of documents) {
    step("ingesting", document.name);
    const response = await fetch(`${baseUrl}/${document.file}`);
    if (!response.ok) {
      throw new Error(`Could not fetch ${document.file} (${response.status}).`);
    }

    let accepted: DocumentSummary | null;
    if (document.kind === "text") {
      accepted = await ingestText(instance, {
        name: document.name,
        text: await response.text(),
        format: document.format ?? "markdown",
        link,
      });
    } else {
      const blob = await response.blob();
      accepted = await ingestFile(instance, new File([blob], document.name), {
        name: document.name,
        link,
      });
    }

    if (!accepted) {
      throw new Error(`Ingesting '${document.name}' returned no document.`);
    }
    const finished = await awaitDocument(instance, accepted.documentId, document.name, step);
    chunksCreated += finished.chunkCount;
  }

  return { documentsIngested: documents.length, chunksCreated };
}

/**
 * Runs the full ingest. `wipeFirst` must be true when the graph is non-empty (import
 * requires an empty target — the caller gates this behind a typed confirm). Steps are
 * reported via onStep for a progress line.
 */
export async function loadSampleGraph(
  instance: InstanceConfig,
  entry: SampleManifestEntry,
  baseUrl: string,
  options: { wipeFirst: boolean; onStep?: LoadStepReporter },
): Promise<LoadResult> {
  const step = options.onStep ?? (() => {});

  if (options.wipeFirst) {
    step("wiping");
    await tabulaRasa(instance);
  }

  step("fetching");
  const fileResponse = await fetch(`${baseUrl}/${entry.file}`);
  if (!fileResponse.ok) {
    throw new Error(`Could not fetch ${entry.file} (${fileResponse.status}).`);
  }
  const jsonl = await fileResponse.blob();

  step("importing");
  const imported = await importBulk(instance, jsonl);

  step("indexing");
  for (const recipe of entry.indexRecipes) {
    const created = await createIndex(instance, recipe);
    if (!created) {
      throw new Error(
        `Index '${recipe.uniqueId}' was not created (duplicate id or REST-inexpressible options).`,
      );
    }
  }

  // The three steps below only run for a sample that carries them, so the load path for the
  // baked datasets is unchanged.
  let tagsSeeded = 0;
  if (entry.indexSeeds?.length) {
    step("seeding");
    tagsSeeded = await seedIndexes(
      instance,
      entry.indexSeeds,
      // Prefer what the import reported, but never let a 0/absent count disable the guard:
      // `?? ` alone would accept `verticesCreated: 0` and make truncation undetectable.
      Math.max(imported?.verticesCreated ?? 0, entry.vertexCount),
      step,
    );
  }

  let documentsIngested = 0;
  let chunksCreated = 0;
  if (entry.documents?.length) {
    step("binding");
    const binding = await ensureDocumentBinding(instance);
    if (!binding?.ready) {
      const roles = binding
        ? [binding.vector, binding.fulltext, binding.entity]
            .filter((role) => role?.required && !role.ready)
            .map((role) => `${role.role} (${role.indexId})${role.detail ? `: ${role.detail}` : ""}`)
            .join(", ")
        : "no binding returned";
      throw new Error(`The semantic layer could not be bound. Not ready: ${roles}.`);
    }

    const ingested = await ingestDocuments(instance, entry, baseUrl, step);
    documentsIngested = ingested.documentsIngested;
    chunksCreated = ingested.chunksCreated;
  }

  step("rendering");
  const graph = await getGraph(instance, CANVAS_ELEMENT_CAP);

  return {
    graph: graph ?? { vertices: [], edges: [] },
    verticesCreated: imported?.verticesCreated ?? 0,
    edgesCreated: imported?.edgesCreated ?? 0,
    tagsSeeded,
    documentsIngested,
    chunksCreated,
  };
}

export type EmbeddingGate =
  | { kind: "not-embedded" }
  | { kind: "ready" }
  | { kind: "provider-off" }
  | { kind: "mismatch"; detail: string };

/**
 * Whether the sample's text-in features (semantic search, queryText) will work on this
 * instance. Bring-your-own-vector always works (the vectors are in the file); this only
 * gates TEXT-IN, which needs a provider whose identity matches the baked vectors.
 */
export function embeddingGate(
  embedding: SampleEmbeddingInfo | null,
  status: StatusREST | null,
): EmbeddingGate {
  if (!embedding) return { kind: "not-embedded" };
  const provider = status?.embedding;
  if (!provider || !provider.enabled) return { kind: "provider-off" };
  if (provider.dimension !== embedding.dimension) {
    return {
      kind: "mismatch",
      detail: `provider is ${provider.dimension}-dim, the dataset is ${embedding.dimension}-dim`,
    };
  }
  if (provider.modelName && !embedding.model.startsWith(provider.modelName)) {
    return {
      kind: "mismatch",
      detail: `provider model '${provider.modelName}' differs from the dataset's '${embedding.model}'`,
    };
  }
  return { kind: "ready" };
}

/**
 * Whether a sample that ingests DOCUMENTS can run on this instance (feature knowledge-demo).
 * `blocking: false` means the sample still loads, just with less in it: NLP enrichment is
 * additive, so a missing sidecar costs the entity network and nothing else.
 */
export type IngestionGate =
  | { kind: "not-needed"; blocking: false }
  | { kind: "ready"; blocking: false }
  | { kind: "nlp-off"; blocking: false; detail: string }
  | { kind: "status-unknown"; blocking: true; detail: string }
  | { kind: "ingestion-off"; blocking: true; detail: string }
  | { kind: "provider-off"; blocking: true; detail: string }
  | { kind: "docling-unreachable"; blocking: true; detail: string };

export function ingestionGate(
  entry: SampleManifestEntry,
  status: StatusREST | null,
): IngestionGate {
  if (!entry.documents?.length) return { kind: "not-needed", blocking: false };

  // Block on unknown state, but do NOT invent a reason for it. `/status` has not resolved on
  // first mount, and it stays null for an instance that is unreachable or whose namespace was
  // dropped; claiming "ingestion is off, set F8_INGESTION=true" in those cases sends the user
  // to fix a setting that is very likely already correct.
  if (!status) {
    return {
      kind: "status-unknown",
      blocking: true,
      detail: "this instance's capability state has not loaded yet.",
    };
  }

  const ingestion = status.ingestion;
  if (!ingestion?.enabled) {
    return {
      kind: "ingestion-off",
      blocking: true,
      detail:
        "this instance has unstructured ingestion off, so every /document route answers 403. " +
        "Set F8_INGESTION=true (it is the compose default) and restart.",
    };
  }

  // Ingestion embeds chunk text through the provider, and this sample's whole point is fused
  // retrieval, so a provider-less load would be a demo of half the feature.
  if (!status?.embedding?.enabled) {
    return {
      kind: "provider-off",
      blocking: true,
      detail:
        "the embedding provider is off, so chunks cannot be embedded and fused search would " +
        "degrade to lexical only. Set F8_EMBEDDINGS=true (the compose default).",
    };
  }

  if (entry.documents.some((d) => d.kind === "binary") && !ingestion.docling?.reachable) {
    return {
      kind: "docling-unreachable",
      blocking: true,
      detail: ingestion.docling?.configured
        ? "this sample uploads a PDF and a spreadsheet, which convert in the docling sidecar, " +
          "and it is configured but not answering. Check that the container is up."
        : "this sample uploads a PDF and a spreadsheet, which convert in the docling sidecar, " +
          "and no endpoint is configured for it. Start the compose environment.",
    };
  }

  const nlp = status?.nlp;
  if (!nlp?.enabled || !nlp.reachable) {
    return {
      kind: "nlp-off",
      blocking: false,
      detail:
        "the NLP sidecar is off or unreachable, so the documents will load WITHOUT the entity " +
        "network (enrichment is additive and never fails an ingest). Set F8_NLP=true for it.",
    };
  }

  return { kind: "ready", blocking: false };
}
