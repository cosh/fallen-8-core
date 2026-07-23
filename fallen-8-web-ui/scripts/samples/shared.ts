/**
 * Shared building blocks for the sample-graph generators (feature sample-graphs):
 * the BuiltSample shape, a deterministic PRNG, the embedding helper (POST
 * /embedding/text — the same provider path Fallen-8 uses), and small math.
 */

import { prop, type JsonlProperty } from "../../src/lib/jsonlGraph";
import type { SampleManifestEntry } from "../../src/lib/samples";

export interface BuiltSample {
  entry: SampleManifestEntry;
  jsonl: string;
}

export const F8_BASE = process.env.F8_BASE ?? "http://localhost:5078";

/** The embedding name every dataset uses, and the bound-index option value. */
export const EMBEDDING_NAME = "default";

/**
 * Mulberry32 — a tiny deterministic PRNG so seeded datasets (viewers, attack estates)
 * reproduce byte-for-byte. `Math.random` would make every rebuild a diff.
 */
export function seededRandom(seed: number): () => number {
  let state = seed >>> 0;
  return () => {
    state |= 0;
    state = (state + 0x6d2b79f5) | 0;
    let t = Math.imul(state ^ (state >>> 15), 1 | state);
    t = (t + Math.imul(t ^ (t >>> 7), 61 | t)) ^ t;
    return ((t ^ (t >>> 14)) >>> 0) / 4294967296;
  };
}

/** Deterministic pick from an array. */
export function pick<T>(rng: () => number, items: readonly T[]): T {
  return items[Math.floor(rng() * items.length)];
}

/** Great-circle distance in kilometres. */
export function haversineKm(
  lat1: number,
  lon1: number,
  lat2: number,
  lon2: number,
): number {
  const toRad = (deg: number) => (deg * Math.PI) / 180;
  const dLat = toRad(lat2 - lat1);
  const dLon = toRad(lon2 - lon1);
  const a =
    Math.sin(dLat / 2) ** 2 +
    Math.cos(toRad(lat1)) * Math.cos(toRad(lat2)) * Math.sin(dLon / 2) ** 2;
  return Math.round(6371 * 2 * Math.atan2(Math.sqrt(a), Math.sqrt(1 - a)));
}

interface EmbedResponse {
  model: string;
  dimension: number;
  vectors: number[][];
}

/**
 * Embeds texts through the running instance's provider (POST /embedding/text), batched
 * at the provider's default MaxBatchSize (64). Returns the vectors AND the model stamp
 * (`name[@version]#dimension#metric`) so the datasets carry honest provenance next to
 * each vector. Throws a clear message when the provider is off — embedded datasets
 * need a compose environment (or a local instance wired to Ollama's bge-m3).
 */
export async function embedTexts(
  texts: string[],
): Promise<{ vectors: number[][]; model: string; dimension: number }> {
  const status = await (await fetch(`${F8_BASE}/status`)).json();
  if (!status.embedding?.enabled) {
    throw new Error(
      `the embedding provider is OFF at ${F8_BASE} (status.embedding.enabled=false). ` +
        "Embedded samples need it — run the compose environment, or start a local instance " +
        "with Fallen8__Embedding__Enabled=true and Backend=Ollama pointing at bge-m3.",
    );
  }

  // Small batches: a CPU Ollama (the default local/compose backend) embeds bge-m3 at a
  // few texts/second, and Fallen-8's provider HttpClient times out at 100s — a 64-text
  // batch blows past that. F8_EMBED_BATCH lets a GPU environment raise it.
  const batchSize = Number(process.env.F8_EMBED_BATCH) || 8;
  const vectors: number[][] = [];
  let model = "";
  let dimension = 0;

  for (let i = 0; i < texts.length; i += batchSize) {
    const batch = texts.slice(i, i + batchSize);
    const data = await postEmbed(batch);
    model = data.model;
    dimension = data.dimension;
    vectors.push(...data.vectors);
    process.stdout.write(`\r  embedded ${Math.min(i + batchSize, texts.length)}/${texts.length}`);
  }
  process.stdout.write("\n");

  return { vectors, model, dimension };
}

/** One /embedding/text call, retried once on the first-call lazy-load timeout. */
async function postEmbed(texts: string[]): Promise<EmbedResponse> {
  for (let attempt = 0; attempt < 2; attempt++) {
    const response = await fetch(`${F8_BASE}/embedding/text`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ texts }),
    });
    if (response.ok) return (await response.json()) as EmbedResponse;
    const body = await response.text();
    // The first call loads the model (can exceed the provider's 100s timeout on CPU);
    // by the retry the model is warm. Anything else is a real failure.
    const retryable = response.status === 500 && body.includes("Timeout");
    if (!retryable || attempt === 1) {
      throw new Error(`/embedding/text failed (${response.status}): ${body}`);
    }
    process.stdout.write("\n  (model warming up — retrying)\n");
  }
  throw new Error("unreachable");
}

/** The reserved embedding + model-stamp property pair for one element's vector. */
export function embeddingProperties(
  vector: number[],
  model: string,
): Record<string, JsonlProperty> {
  return {
    [`$embedding:${EMBEDDING_NAME}`]: prop.singleArray(vector),
    [`$embeddingModel:${EMBEDDING_NAME}`]: prop.string(model),
  };
}

/**
 * A bound VectorIndex recipe over the dataset's embeddings. The `model` option is left
 * unset on purpose: binding an identity would make /embedding/search 409 unless the live
 * provider's stamp matched exactly, and a demo should work against any bge-m3-class
 * provider — the per-element $embeddingModel stamp still records true provenance.
 */
export function boundVectorIndexRecipe(dimension: number, indexId = "embeddings") {
  return {
    uniqueId: indexId,
    pluginType: "VectorIndex",
    pluginOptions: {
      dimension: { propertyId: "dimension", propertyValue: String(dimension), fullQualifiedTypeName: "System.Int32" },
      metric: { propertyId: "metric", propertyValue: "Cosine", fullQualifiedTypeName: "System.String" },
      embeddingName: { propertyId: "embeddingName", propertyValue: EMBEDDING_NAME, fullQualifiedTypeName: "System.String" },
    },
  };
}
