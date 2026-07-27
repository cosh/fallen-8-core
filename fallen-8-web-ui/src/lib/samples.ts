// MIT License
//
// samples.ts
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
 * Sample-graph manifest types (feature sample-graphs): the contract between the build
 * script (scripts/build-samples.ts, which writes public/samples/index.json) and the
 * Dashboard's Sample graphs section, which renders the card grid and runs the loader
 * purely from this data. Adding a static sample is a script + manifest change — no UI
 * code.
 */

import type { StyleConfig } from "../canvas/styleConfig";

/** What a sample shows off — rendered as card badges. */
export type SampleBadge = "canvas" | "path" | "analytics" | "semantic" | "spatial";

/** A POST /index recipe the loader creates right after import. */
export interface SampleIndexRecipe {
  uniqueId: string;
  pluginType: string;
  // Values match PropertySpecification (propertyId repeats the key, as the REST surface expects).
  pluginOptions: Record<
    string,
    { propertyId: string; propertyValue: string; fullQualifiedTypeName: string }
  >;
}

/** Provenance of the dataset's precomputed vectors, compared against /status.embedding. */
export interface SampleEmbeddingInfo {
  name: string;
  model: string;
  dimension: number;
  metric: string;
}

export interface SampleManifestEntry {
  id: string;
  title: string;
  emoji: string;
  pitch: string;
  vertexCount: number;
  edgeCount: number;
  badges: SampleBadge[];
  /** Suggested next steps, shown after a successful load. */
  trySteps: string[];
  /** Same-origin asset path, e.g. "samples/karate-club.jsonl". */
  file: string;
  /** Applied to the instance's canvas style config on load. */
  styleConfig: Partial<StyleConfig>;
  indexRecipes: SampleIndexRecipe[];
  embedding: SampleEmbeddingInfo | null;
}

export interface SamplesManifest {
  version: 1;
  samples: SampleManifestEntry[];
}
