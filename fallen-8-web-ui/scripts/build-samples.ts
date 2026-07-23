/**
 * Builds the Studio's sample-graph datasets (feature sample-graphs) into
 * public/samples/: one fallen8-jsonl file per sample plus the index.json manifest the
 * Dashboard card grid renders from. Deterministic: fixed seeds, fixed creationDate —
 * re-running without a definition change reproduces every file byte-identically.
 *
 *   npm run build:samples                  # build everything
 *   npm run build:samples -- --only karate-club
 *   npm run build:samples -- --verify     # also round-trip each file through a live,
 *                                         # EMPTY instance (F8_BASE, default
 *                                         # http://localhost:5078) and compare counts
 *
 * Samples with embeddings (phase 4) additionally need the instance's embedding
 * provider (compose environment) — the script embeds via POST /embedding/text, the
 * same provider path Fallen-8 itself uses.
 */

import { mkdirSync, writeFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import { buildJsonlGraph, type JsonlEdge, type JsonlVertex, prop } from "../src/lib/jsonlGraph";
import type { SampleManifestEntry, SamplesManifest } from "../src/lib/samples";

const OUT_DIR = join(dirname(fileURLToPath(import.meta.url)), "..", "public", "samples");
const F8_BASE = process.env.F8_BASE ?? "http://localhost:5078";

interface BuiltSample {
  entry: SampleManifestEntry;
  jsonl: string;
}

// ---------------------------------------------------------------------------
// karate-club — Zachary (1977), the community-detection classic
// ---------------------------------------------------------------------------

/** The canonical 78-edge list (1-indexed members) from Zachary's 1977 study. */
const KARATE_EDGES: ReadonlyArray<readonly [number, number]> = [
  [2, 1], [3, 1], [3, 2], [4, 1], [4, 2], [4, 3], [5, 1], [6, 1], [7, 1], [7, 5],
  [7, 6], [8, 1], [8, 2], [8, 3], [8, 4], [9, 1], [9, 3], [10, 3], [11, 1], [11, 5],
  [11, 6], [12, 1], [13, 1], [13, 4], [14, 1], [14, 2], [14, 3], [14, 4], [17, 6], [17, 7],
  [18, 1], [18, 2], [20, 1], [20, 2], [22, 1], [22, 2], [26, 24], [26, 25], [28, 3], [28, 24],
  [28, 25], [29, 3], [30, 24], [30, 27], [31, 2], [31, 9], [32, 1], [32, 25], [32, 26], [32, 29],
  [33, 3], [33, 9], [33, 15], [33, 16], [33, 19], [33, 21], [33, 23], [33, 24], [33, 30], [33, 31],
  [33, 32], [34, 9], [34, 10], [34, 14], [34, 15], [34, 16], [34, 19], [34, 20], [34, 21], [34, 23],
  [34, 24], [34, 27], [34, 28], [34, 29], [34, 30], [34, 31], [34, 32], [34, 33],
];

/** The real post-split membership (ground truth for the label-propagation demo). */
const MR_HI_FACTION = new Set([1, 2, 3, 4, 5, 6, 7, 8, 11, 12, 13, 14, 17, 18, 20, 22]);

function buildKarateClub(): BuiltSample {
  if (KARATE_EDGES.length !== 78) {
    throw new Error(`karate-club: expected the canonical 78 edges, got ${KARATE_EDGES.length}`);
  }

  const vertices: JsonlVertex[] = [];
  for (let member = 1; member <= 34; member++) {
    const name = member === 1 ? "Mr. Hi" : member === 34 ? "Officer" : `Member ${member}`;
    const icon = member === 1 ? "🧑‍🏫" : member === 34 ? "👔" : "🥋";
    vertices.push({
      id: member,
      label: "member",
      properties: {
        name: prop.string(name),
        faction: prop.string(MR_HI_FACTION.has(member) ? "mr-hi" : "officer"),
        icon: prop.string(icon),
      },
    });
  }

  const edges: JsonlEdge[] = KARATE_EDGES.map(([source, target], index) => ({
    id: 100 + index,
    source,
    target,
    edgePropertyId: "interactsWith",
  }));

  return {
    jsonl: buildJsonlGraph(vertices, edges),
    entry: {
      id: "karate-club",
      title: "Zachary's Karate Club",
      emoji: "🥋",
      pitch:
        "The most famous graph in community detection: 34 club members, 78 friendships, one legendary split (Zachary, 1977).",
      vertexCount: vertices.length,
      edgeCount: edges.length,
      badges: ["canvas", "analytics", "path"],
      trySteps: [
        "Analytics → LABELPROPAGATION with a write-back property, then color the canvas by it — the computed communities reproduce the club's real 1977 split (compare with color by 'faction').",
        "Analytics → TRIANGLECOUNT and WCC on the textbook graph.",
        "Path → route from Mr. Hi to the Officer (look their ids up on the Browser screen).",
      ],
      file: "samples/karate-club.jsonl",
      styleConfig: {
        nodeColorMode: "property",
        nodeColorProperty: "faction",
        nodeSizeMode: "degree",
        nodeImageProperty: "icon",
        edgeArrows: false,
      },
      indexRecipes: [],
      embedding: null,
    },
  };
}

// ---------------------------------------------------------------------------
// pipeline
// ---------------------------------------------------------------------------

const REGISTRY: Record<string, () => BuiltSample | Promise<BuiltSample>> = {
  "karate-club": buildKarateClub,
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

  // Clean up so the next sample verifies against an empty graph again.
  await fetch(`${F8_BASE}/tabularasa?waitForCompletion=true`, { method: "HEAD" });
  console.log(`  verified: imported ${result.verticesCreated}V/${result.edgesCreated}E and wiped`);
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
  const entries: SampleManifestEntry[] = [];

  for (const id of ids) {
    console.log(`building ${id}…`);
    const sample = await REGISTRY[id]();
    writeFileSync(join(OUT_DIR, `${id}.jsonl`), sample.jsonl, "utf8");
    entries.push(sample.entry);
    console.log(
      `  wrote ${sample.entry.file} (${sample.entry.vertexCount}V/${sample.entry.edgeCount}E, ${(sample.jsonl.length / 1024).toFixed(1)} KiB)`,
    );
    if (verify) {
      await verifyRoundTrip(sample);
    }
  }

  // --only rebuilds one file but the manifest always describes the full registry: merge
  // the untouched entries from the registry by rebuilding them in memory (cheap for the
  // deterministic generators; network-dependent samples should be rebuilt explicitly).
  if (only) {
    for (const id of Object.keys(REGISTRY)) {
      if (id !== only) {
        entries.push((await REGISTRY[id]()).entry);
      }
    }
    entries.sort(
      (a, b) => Object.keys(REGISTRY).indexOf(a.id) - Object.keys(REGISTRY).indexOf(b.id),
    );
  }

  const manifest: SamplesManifest = { version: 1, samples: entries };
  writeFileSync(join(OUT_DIR, "index.json"), JSON.stringify(manifest, null, 2) + "\n", "utf8");
  console.log(`wrote index.json (${entries.length} samples)`);
}

main().catch((error) => {
  console.error(error);
  process.exit(1);
});
