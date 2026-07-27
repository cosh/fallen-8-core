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
 * Sample-graph loader (feature sample-graphs). Fetches a curated dataset from its PUBLIC
 * GitHub raw URL just-in-time and ingests it: (optional wipe) → POST /bulk/import → create
 * the manifest's index recipes → hand the imported elements back for the canvas. No
 * embedding work happens here — the vectors are baked into the fallen8-jsonl file and a
 * bound VectorIndex projects them on import.
 */

import type { InstanceConfig } from "../instances/types";
import type { GraphREST, StatusREST } from "../api/types";
import { createIndex, getGraph, importBulk, tabulaRasa } from "../api/endpoints";
import type { SampleEmbeddingInfo, SampleManifestEntry, SamplesManifest } from "./samples";

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

/** Upper bound on elements re-read into the canvas after import (all file samples fit). */
const CANVAS_ELEMENT_CAP = 20_000;

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

export type LoadStep = "wiping" | "fetching" | "importing" | "indexing" | "rendering";

export interface LoadResult {
  graph: GraphREST;
  verticesCreated: number;
  edgesCreated: number;
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
  options: { wipeFirst: boolean; onStep?: (step: LoadStep) => void },
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

  step("rendering");
  const graph = await getGraph(instance, CANVAS_ELEMENT_CAP);

  return {
    graph: graph ?? { vertices: [], edges: [] },
    verticesCreated: imported?.verticesCreated ?? 0,
    edgesCreated: imported?.edgesCreated ?? 0,
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
