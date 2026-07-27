// MIT License
//
// mockGraph.ts
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
 * The first-run show's canned mock graph (feature studio-first-run): "Asymmetric Cyber Warfare".
 *
 * A tiny, story-driven graph: an elite threat actor weaponizes a compromised supply-chain tool
 * to strike multiple targets, while a SOC and its analyst defend. Six entities, five directed
 * relationships. It tells the graph pitch in one glance: the full blast radius of a compromise
 * is a single native traversal here, versus brittle multi-table joins in a relational store.
 *
 * This is the show's ASSET SEAM: pure, hardcoded data drawn as SVG by <FirstRunShow>. It is
 * never the user's data and is never read from or written to the engine. The same graph ships
 * as a loadable sample (scripts/samples/cyberWarfare.ts); a designer who later produces a
 * Lottie/.riv can replace the code animation behind this boundary without touching the
 * empty-state detection or the replay wiring.
 *
 * Positions are laid out by hand in the viewBox so the attack fans out left-to-right and the
 * defenders sit below. `rank` is a precomputed mock PageRank (0..1) that drives the analytics
 * beat's sizing; `emoji` is the node glyph.
 */

export interface MockVertex {
  id: number;
  label: string;
  emoji: string;
  x: number;
  y: number;
  rank: number;
}

export interface MockEdge {
  id: string;
  source: number;
  target: number;
  label: string;
}

export interface MockGraph {
  viewBox: string;
  vertices: readonly MockVertex[];
  edges: readonly MockEdge[];
  /** Path beat: the highlighted route (the attack's blast radius), as an ordered vertex-id list. */
  path: readonly number[];
  /** Subgraph beat: the member vertex ids; member edges are those with both endpoints inside. */
  subgraph: readonly number[];
  /** Semantic beat: the query point, the "nearest" vertices, and the neighbor expanded into. */
  semantic: {
    query: { x: number; y: number };
    nearest: readonly number[];
    expandFrom: number;
    expandTo: number;
  };
}

const V = (
  id: number,
  label: string,
  emoji: string,
  x: number,
  y: number,
  rank: number,
): MockVertex => ({ id, label, emoji, x, y, rank });

const E = (source: number, target: number, label: string): MockEdge => ({
  id: `${source}-${target}`,
  source,
  target,
  label,
});

export const MOCK_GRAPH: MockGraph = {
  viewBox: "0 0 820 480",
  vertices: [
    V(1, "Threat Actor", "🦹", 120, 140, 0.5),
    V(2, "Supply Chain", "📦", 390, 210, 0.95),
    V(3, "Critical Infra", "🏭", 690, 140, 0.82),
    V(4, "Gov Agency", "🏛️", 700, 330, 0.4),
    V(5, "SOC", "🛡️", 170, 385, 0.45),
    V(6, "Analyst", "🧑‍💻", 435, 400, 0.6),
  ],
  edges: [
    E(1, 2, "supplies trojan"),
    E(2, 3, "delivers payload to"),
    E(2, 4, "delivers payload to"),
    E(5, 6, "employs"),
    E(6, 3, "investigates"),
  ],
  // Blast radius: Threat Actor → Supply Chain tool → Critical Infrastructure.
  path: [1, 2, 3],
  // The compromised tool plus everything it delivers to.
  subgraph: [2, 3, 4],
  // Query sits between the two targets; its nearest are the tool and the two targets, then the
  // neighborhood expands from the tool back to the threat actor that supplied it.
  semantic: { query: { x: 690, y: 235 }, nearest: [3, 4, 2], expandFrom: 2, expandTo: 1 },
};

/** Whether an edge lies on the highlighted path (directed, consecutive), for the path beat. */
export function isPathEdge(edge: MockEdge, path: readonly number[]): boolean {
  for (let i = 0; i < path.length - 1; i++) {
    if (edge.source === path[i] && edge.target === path[i + 1]) return true;
  }
  return false;
}

/** Whether an edge is inside the subgraph (both endpoints are members), for the subgraph beat. */
export function isSubgraphEdge(edge: MockEdge, subgraph: readonly number[]): boolean {
  return subgraph.includes(edge.source) && subgraph.includes(edge.target);
}
