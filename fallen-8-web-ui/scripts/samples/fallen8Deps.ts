// MIT License
//
// fallen8Deps.ts
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
 * fallen8-deps — Fallen-8's own dependency graph (feature sample-graphs). The static
 * twin of the dynamic GitHub card: both run the shared sbomToGraph transform. No
 * embeddings (dependency metadata is not prose).
 *
 * The SBOM is fetched ONCE and committed (scripts/samples/data/fallen8-sbom.json), then
 * reused on every build — GitHub's SBOM endpoint is rate-limited and the graph only
 * changes when dependencies do. The committed copy is refreshed by CI when a dependency
 * manifest changes (.github/workflows/refresh-sbom.yml) or on demand:
 *   F8_DEPS_REFETCH=1 npm run build:samples -- --only fallen8-deps
 *
 * A refetch is CANONICALIZED and then compared before it is written, because the endpoint's
 * answer differs on every call whether or not a dependency moved: a fresh documentNamespace
 * UUID, a fresh creationInfo timestamp, a creators entry carrying the generator's own build
 * id, and the packages in no stable order. Left alone, that rewrote ~11,600 lines of two
 * committed files on every CI run, ten times in one month, the last of which changed four
 * packages of 1086. See features/done/sidecar-shared-options/spec.md §7.
 */

import { readFileSync, writeFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import { buildJsonlGraph } from "../../src/lib/jsonlGraph";
import { sbomToGraph, type SpdxSbom } from "../../src/lib/sbomGraph";
import type { BuiltSample } from "./shared";

const DEFAULT_REPO = "cosh/fallen-8-core";
const SBOM_PATH = join(dirname(fileURLToPath(import.meta.url)), "data", "fallen8-sbom.json");

/**
 * Ordinal rather than locale-aware, for the reason the engine's own sorted responses give:
 * a culture-sensitive order would rank the same two identifiers differently on different
 * machines, which is exactly the churn this is here to remove.
 */
function ordinal(a: string, b: string): number {
  return a < b ? -1 : a > b ? 1 : 0;
}

/**
 * The same document with its unordered collections put in a fixed order. SPDX defines both
 * `packages` and `relationships` as sets, so ordering them changes nothing about what the
 * document says, and it makes the DERIVED sample stable too, because sbomToGraph assigns
 * vertex ids by package position ("packages keep their SBOM order for stable ids", which was
 * true of the transform and false of its input).
 *
 * The comparators are total rather than relying on sort stability: SPDXID is meant to be
 * unique, and if it ever is not, a tie broken by input order would reintroduce the churn.
 */
export function canonicalizeSbom(sbom: SpdxSbom): SpdxSbom {
  const canonical: SpdxSbom = { ...sbom };
  if (sbom.packages) {
    canonical.packages = [...sbom.packages].sort(
      (a, b) =>
        ordinal(a.SPDXID ?? "", b.SPDXID ?? "") ||
        ordinal(a.name ?? "", b.name ?? "") ||
        ordinal(a.versionInfo ?? "", b.versionInfo ?? ""),
    );
  }
  if (sbom.relationships) {
    canonical.relationships = [...sbom.relationships].sort(
      (a, b) =>
        ordinal(a.spdxElementId ?? "", b.spdxElementId ?? "") ||
        ordinal(a.relatedSpdxElement ?? "", b.relatedSpdxElement ?? "") ||
        ordinal(a.relationshipType ?? "", b.relationshipType ?? ""),
    );
  }
  return canonical;
}

/**
 * JSON with every OBJECT KEY in a fixed order, for comparison only, never for what is written.
 *
 * `JSON.stringify` emits keys in insertion order, so two documents that say the same thing with
 * their fields in a different order stringify differently. That is not hypothetical here: the
 * `creators` entry shows the endpoint's generator is itself versioned (protobom, with a build
 * date in its name), and a generator upgrade that reorders struct fields would otherwise read as
 * "the dependencies changed" and rewrite 11,600 lines to say nothing. Measured before this
 * existed: reordering the keys inside ONE package made the comparison report a change.
 *
 * ARRAY order is deliberately left alone. Every array in this document is an SPDX set, so sorting
 * them all would also be defensible, but `packages` and `relationships` are already canonical,
 * and `externalRefs` is read positionally by the transform (`parsePurl` takes the FIRST purl
 * reference), so reordering it is a behaviour change rather than a normalization. If an upgrade
 * ever reorders THAT, the cost is one bounded rewrite, which is the honest trade against masking a
 * real difference.
 */
function stableStringify(value: unknown): string {
  if (Array.isArray(value)) {
    return "[" + value.map(stableStringify).join(",") + "]";
  }
  if (value !== null && typeof value === "object") {
    const record = value as Record<string, unknown>;
    return (
      "{" +
      Object.keys(record)
        .sort()
        .map((key) => JSON.stringify(key) + ":" + stableStringify(record[key]))
        .join(",") +
      "}"
    );
  }
  // undefined stringifies to undefined rather than a string, which would poison the join.
  return JSON.stringify(value) ?? "null";
}

/**
 * Whether two documents say the same thing about this repository's dependencies, ignoring the
 * three fields the endpoint regenerates per call: `documentNamespace` (a fresh UUID),
 * `creationInfo.created` (now) and `creationInfo.creators` (which carries the generator's own
 * build id). None of those is a dependency, so none of them is a reason to rewrite the file.
 *
 * Both sides are canonicalized first, so a pure reordering of packages or relationships counts as
 * equal, and both are stringified key-stably, so a reordering of FIELDS does too.
 */
export function sbomContentEquals(left: SpdxSbom, right: SpdxSbom): boolean {
  const strip = (sbom: SpdxSbom) => {
    const { documentNamespace: _ns, creationInfo: _info, ...content } = canonicalizeSbom(sbom) as
      SpdxSbom & { documentNamespace?: unknown; creationInfo?: unknown };
    return stableStringify(content);
  };
  return strip(left) === strip(right);
}

/** The committed copy, or null when there is none yet or it cannot be parsed. */
function committedSbom(): SpdxSbom | null {
  try {
    return JSON.parse(readFileSync(SBOM_PATH, "utf8")) as SpdxSbom;
  } catch {
    // A missing or corrupt committed copy is not an error here: the refetch that is already in
    // flight replaces it, and a non-refetch build has nothing to fall back to and fails below
    // on its own readFileSync with the real reason.
    return null;
  }
}

async function loadSbom(): Promise<SpdxSbom> {
  if (process.env.F8_DEPS_REFETCH === "1") {
    const repo = process.env.F8_DEPS_REPO ?? DEFAULT_REPO;
    const url = `https://api.github.com/repos/${repo}/dependency-graph/sbom`;
    const headers: Record<string, string> = { Accept: "application/vnd.github+json" };
    // CI passes the repo's GITHUB_TOKEN so the refresh does not share the anonymous
    // per-IP rate limit; a local refetch works fine without one (public repo).
    if (process.env.F8_DEPS_TOKEN) headers.Authorization = `Bearer ${process.env.F8_DEPS_TOKEN}`;
    const response = await fetch(url, { headers });
    if (!response.ok) {
      throw new Error(
        `fallen8-deps: SBOM refetch failed (${response.status}) for ${repo}: ${await response.text()}`,
      );
    }
    const fetched = canonicalizeSbom(((await response.json()) as { sbom: SpdxSbom }).sbom);
    const committed = committedSbom();

    if (committed && sbomContentEquals(committed, fetched)) {
      // Deliberately NOT written. The committed copy keeps its own metadata, so its timestamp
      // goes on meaning "when this SBOM last actually changed" rather than "when CI last
      // looked", and the workflow's `git status --porcelain` guard finds nothing to commit.
      // The canonicalized COMMITTED copy is returned, not the file as it sits on disk, so a
      // build that runs before the first sorted write still derives the same sample.
      console.log(`  refetched; dependencies unchanged, ${SBOM_PATH} left as it is`);
      return canonicalizeSbom(committed);
    }

    // Changed (or nothing committed yet): written whole, fresh metadata included.
    writeFileSync(SBOM_PATH, JSON.stringify(fetched, null, 1) + "\n", "utf8");
    console.log(`  refetched and stored ${SBOM_PATH} (dependencies changed)`);
    return fetched;
  }
  // Canonicalized on read as well, so the sample a plain build produces cannot depend on
  // whether the committed file happens to be sorted yet.
  return canonicalizeSbom(JSON.parse(readFileSync(SBOM_PATH, "utf8")) as SpdxSbom);
}

export async function buildFallen8Deps(): Promise<BuiltSample> {
  const sbom = await loadSbom();
  const { vertices, edges, ecosystemCounts } = sbomToGraph(sbom);

  const ecosystems = Object.entries(ecosystemCounts)
    .sort((a, b) => b[1] - a[1])
    .map(([name, count]) => `${name} ${count}`)
    .join(", ");

  return {
    jsonl: buildJsonlGraph(vertices, edges),
    entry: {
      id: "fallen8-deps",
      title: "Fallen-8 Dependencies",
      emoji: "📦",
      pitch: `Fallen-8's own supply chain across every ecosystem (${ecosystems}) — the static twin of the GitHub card below.`,
      vertexCount: vertices.length,
      edgeCount: edges.length,
      badges: ["canvas", "analytics"],
      trySteps: [
        "Analytics → PAGERANK to rank the most-depended-on packages.",
        "Analytics → WCC to see each ecosystem fall out as its own component.",
        "Canvas → color by 'license' or 'ecosystem', size by in-degree, and the 'icon' emoji renders per ecosystem.",
      ],
      file: "fallen8-deps.jsonl",
      styleConfig: {
        nodeColorMode: "property",
        nodeColorProperty: "ecosystem",
        nodeSizeMode: "in-degree",
        nodeImageProperty: "icon",
        edgeArrows: true,
      },
      indexRecipes: [],
      embedding: null,
    },
  };
}
