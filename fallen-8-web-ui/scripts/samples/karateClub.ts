/**
 * karate-club — Zachary (1977), the community-detection classic (feature sample-graphs).
 * 34 members, 78 friendships, the ground-truth faction split. No embeddings, no network:
 * it pins the whole pipeline cheaply and is the reference the loader is tested against.
 */

import { buildJsonlGraph, prop, type JsonlEdge, type JsonlVertex } from "../../src/lib/jsonlGraph";
import type { BuiltSample } from "./shared";

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

export function buildKarateClub(): BuiltSample {
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
      file: "karate-club.jsonl",
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
