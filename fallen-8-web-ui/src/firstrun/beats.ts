// MIT License
//
// beats.ts
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
 * The first-run show's beats (feature studio-first-run). One line of real-text caption per beat
 * (never baked into an image), an endpoint shown as text only, and a story tie-in drawn from the
 * "Asymmetric Cyber Warfare" mock graph. All copy is concise and uses no em dashes.
 *
 * Beats run at 10s each so a newcomer can read each one; the show is skippable and steppable
 * (Prev/Next/dots), so the length is a comfortable ceiling, not a wait.
 */

export type BeatId = "bloom" | "path" | "rank" | "subgraph" | "semantic";

export interface Beat {
  id: BeatId;
  /** Short label for the progress dots / navigation. */
  title: string;
  caption: string;
  /** The real REST endpoint, shown as monospace text only (the show never calls it). */
  endpoint?: string;
  /** A one-line story tie-in for this beat. */
  note?: string;
  durationMs: number;
}

const TEN_SECONDS = 10_000;

export const BEATS: readonly Beat[] = [
  {
    id: "bloom",
    title: "Graph",
    caption: "A graph is entities and the relationships between them, each with typed properties.",
    note: "A threat actor, a compromised tool, its targets, and the defenders.",
    durationMs: TEN_SECONDS,
  },
  {
    id: "path",
    title: "Path",
    caption: "Trace the blast radius: follow the links from one entity to the next.",
    endpoint: "POST /path/{from}/to/{to}",
    note: "Which assets does a compromised supply-chain tool actually reach?",
    durationMs: TEN_SECONDS,
  },
  {
    id: "rank",
    title: "Analytics",
    caption: "Rank what matters most with built-in graph analytics.",
    endpoint: "POST /analytics/PAGERANK",
    note: "The pivot tool and the critical target score highest.",
    durationMs: TEN_SECONDS,
  },
  {
    id: "subgraph",
    title: "Subgraph",
    caption: "Extract a matched pattern as its own standalone, recalculable graph.",
    endpoint: "PUT /subgraph",
    note: "Capture the compromised tool and everything it delivers to.",
    durationMs: TEN_SECONDS,
  },
  {
    id: "semantic",
    title: "Vectors",
    caption: "Search by meaning, then expand the neighborhood.",
    endpoint: "POST /scan/index/vector",
    note: "Vector kNN plus GraphRAG for grounded answers.",
    durationMs: TEN_SECONDS,
  },
];

/** Stable reference for the timeline hook's effect dependency. */
export const BEAT_DURATIONS: readonly number[] = BEATS.map((b) => b.durationMs);
