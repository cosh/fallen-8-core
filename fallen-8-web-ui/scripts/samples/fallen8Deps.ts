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
 */

import { readFileSync, writeFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import { buildJsonlGraph } from "../../src/lib/jsonlGraph";
import { sbomToGraph, type SpdxSbom } from "../../src/lib/sbomGraph";
import type { BuiltSample } from "./shared";

const DEFAULT_REPO = "cosh/fallen-8-core";
const SBOM_PATH = join(dirname(fileURLToPath(import.meta.url)), "data", "fallen8-sbom.json");

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
    const sbom = ((await response.json()) as { sbom: SpdxSbom }).sbom;
    writeFileSync(SBOM_PATH, JSON.stringify(sbom, null, 1) + "\n", "utf8");
    console.log(`  refetched and stored ${SBOM_PATH}`);
    return sbom;
  }
  return JSON.parse(readFileSync(SBOM_PATH, "utf8")) as SpdxSbom;
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
