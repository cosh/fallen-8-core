/**
 * Builds the Studio's sample-graph datasets (feature sample-graphs) into the repo's
 * top-level samples/ directory: one fallen8-jsonl file per sample plus the index.json
 * manifest. Those files are committed and served from a PUBLIC GitHub raw URL; the Studio
 * fetches them just-in-time at ingest and POSTs them to /bulk/import — a standard bulk
 * format, with embeddings PRECOMPUTED here and baked into the file (no embedding work ever
 * happens at ingest time). Deterministic where it can be: fixed seeds and creationDate;
 * the network-sourced samples (air-routes, movie-night posters, fallen8-deps SBOM) pin
 * their inputs (stored SBOM, curated movie list) so rebuilds stay stable.
 *
 *   npm run build:samples                       # build everything
 *   npm run build:samples -- --only karate-club
 *   npm run build:samples -- --verify           # round-trip each file through a live EMPTY
 *                                               # instance (F8_BASE, default :5078)
 *
 * Embedded samples (attack-surface, movie-night, air-routes) additionally need that
 * instance's embedding provider ON (compose environment, or a local instance wired to
 * Ollama bge-m3) — embedding happens HERE, at build time, via POST /embedding/text.
 */

import { mkdirSync, readFileSync, writeFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import type { SampleManifestEntry, SamplesManifest } from "../src/lib/samples";
import { F8_BASE, type BuiltSample } from "./samples/shared";
import { buildKarateClub } from "./samples/karateClub";
import { buildAttackSurface } from "./samples/attackSurface";
import { buildMovieNight } from "./samples/movieNight";
import { buildAirRoutes } from "./samples/airRoutes";
import { buildFallen8Deps } from "./samples/fallen8Deps";

// Repo-root samples/ so the committed files get a clean public raw URL.
const OUT_DIR = join(dirname(fileURLToPath(import.meta.url)), "..", "..", "samples");

/** Build order = card order in the Studio gallery. */
const REGISTRY: Record<string, () => BuiltSample | Promise<BuiltSample>> = {
  "karate-club": buildKarateClub,
  "attack-surface": buildAttackSurface,
  "movie-night": buildMovieNight,
  "air-routes": buildAirRoutes,
  "fallen8-deps": buildFallen8Deps,
};

async function verifyRoundTrip(sample: BuiltSample): Promise<void> {
  const status = await (await fetch(`${F8_BASE}/status`)).json();
  if (status.vertexCount !== 0 || status.edgeCount !== 0) {
    throw new Error(
      `--verify needs an EMPTY instance at ${F8_BASE} (found ${status.vertexCount} vertices); ` +
        "start a fresh one, e.g. dotnet run --project fallen-8-core-apiApp",
    );
  }

  const response = await fetch(`${F8_BASE}/bulk/import`, {
    method: "POST",
    headers: { "Content-Type": "application/x-ndjson" },
    body: sample.jsonl,
  });
  if (!response.ok) {
    throw new Error(`${sample.entry.id}: import failed (${response.status}): ${await response.text()}`);
  }
  const result = (await response.json()) as { verticesCreated: number; edgesCreated: number };
  if (
    result.verticesCreated !== sample.entry.vertexCount ||
    result.edgesCreated !== sample.entry.edgeCount
  ) {
    throw new Error(
      `${sample.entry.id}: round-trip mismatch — manifest says ${sample.entry.vertexCount}V/${sample.entry.edgeCount}E, ` +
        `import created ${result.verticesCreated}V/${result.edgesCreated}E`,
    );
  }

  // Create the sample's index recipes to prove they bind (bound vector index projects
  // the imported vectors — no embedding work here, membership is derived).
  for (const recipe of sample.entry.indexRecipes) {
    const indexResponse = await fetch(`${F8_BASE}/index`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(recipe),
    });
    if (!indexResponse.ok) {
      throw new Error(
        `${sample.entry.id}: index '${recipe.uniqueId}' failed (${indexResponse.status}): ${await indexResponse.text()}`,
      );
    }
  }

  await fetch(`${F8_BASE}/tabularasa?waitForCompletion=true`, { method: "HEAD" });
  console.log(
    `  verified: imported ${result.verticesCreated}V/${result.edgesCreated}E, ` +
      `${sample.entry.indexRecipes.length} index(es) bound, wiped`,
  );
}

async function main(): Promise<void> {
  const args = process.argv.slice(2);
  const verify = args.includes("--verify");
  const onlyIndex = args.indexOf("--only");
  const only = onlyIndex >= 0 ? args[onlyIndex + 1] : null;

  const ids = only ? [only] : Object.keys(REGISTRY);
  const unknown = ids.find((id) => !REGISTRY[id]);
  if (unknown) {
    throw new Error(`unknown sample '${unknown}' (have: ${Object.keys(REGISTRY).join(", ")})`);
  }

  mkdirSync(OUT_DIR, { recursive: true });
  const built = new Map<string, SampleManifestEntry>();

  for (const id of ids) {
    console.log(`building ${id}…`);
    const sample = await REGISTRY[id]();
    writeFileSync(join(OUT_DIR, `${id}.jsonl`), sample.jsonl, "utf8");
    built.set(id, sample.entry);
    console.log(
      `  wrote samples/${id}.jsonl (${sample.entry.vertexCount}V/${sample.entry.edgeCount}E, ${(sample.jsonl.length / 1024).toFixed(1)} KiB)`,
    );
    if (verify) {
      await verifyRoundTrip(sample);
    }
  }

  // The manifest always describes the full registry in build order. Entries NOT built
  // this run (the --only case) are preserved from the existing index.json — never rebuilt
  // (rebuilding would re-embed/re-fetch every other sample, defeating --only).
  const previous = new Map<string, SampleManifestEntry>();
  if (only) {
    try {
      const existing = JSON.parse(readFileSync(join(OUT_DIR, "index.json"), "utf8")) as SamplesManifest;
      for (const entry of existing.samples) previous.set(entry.id, entry);
    } catch {
      console.warn("  (no existing index.json — the manifest will omit un-built samples)");
    }
  }

  const entries: SampleManifestEntry[] = [];
  for (const id of Object.keys(REGISTRY)) {
    const entry = built.get(id) ?? previous.get(id);
    if (entry) entries.push(entry);
    else console.warn(`  manifest omits '${id}' (not built this run and absent from index.json)`);
  }

  const manifest: SamplesManifest = { version: 1, samples: entries };
  writeFileSync(join(OUT_DIR, "index.json"), JSON.stringify(manifest, null, 2) + "\n", "utf8");
  console.log(`wrote samples/index.json (${entries.length} samples)`);
}

main().catch((error) => {
  console.error(error);
  process.exit(1);
});
