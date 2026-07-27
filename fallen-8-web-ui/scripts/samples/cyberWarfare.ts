// MIT License
//
// cyberWarfare.ts
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
 * cyber-warfare - "Asymmetric Cyber Warfare" (feature sample-graphs). Six entities, five
 * directed relationships: a nation-state actor weaponizes a compromised software supply-chain
 * tool to strike multiple targets, while a SOC and its analyst defend. No embeddings, no network
 * (like karate-club), but richly typed so every Studio capability has something real to chew on:
 * per-node `riskScore` and role attributes for scans / coloring, and an `exploitCost` on every
 * edge so Dijkstra finds the cheapest attack chain.
 *
 * It is the same story the first-run show animates (src/firstrun/mockGraph.ts), shipped as a
 * loadable graph so a newcomer can poke the real thing: the full blast radius of a compromise is
 * one native traversal here, versus the brittle multi-table joins a relational store forces.
 */

import { buildJsonlGraph, prop, type JsonlEdge, type JsonlProperty, type JsonlVertex } from "../../src/lib/jsonlGraph";
import type { BuiltSample } from "./shared";

interface Entity {
  id: number;
  label: string;
  name: string;
  icon: string;
  role: "adversary" | "infrastructure" | "target" | "defense";
  riskScore: number;
  description: string;
  /** Label-specific extras, on top of the shared name/role/riskScore/icon/description. */
  extra?: Record<string, JsonlProperty>;
}

const ENTITIES: readonly Entity[] = [
  {
    id: 1,
    label: "threatActor",
    name: "Nation State Actor",
    icon: "🦹",
    role: "adversary",
    riskScore: 95,
    description: "Advanced persistent threat group orchestrating the campaign.",
    extra: { sophistication: prop.string("APT"), origin: prop.string("nation-state") },
  },
  {
    id: 2,
    label: "supplyChainTool",
    name: "Software Supply Chain Tool",
    icon: "📦",
    role: "infrastructure",
    riskScore: 88,
    description:
      "A legitimate build-and-update utility weaponized as the pivot: a trojaned release ships to every downstream consumer.",
    extra: { cve: prop.string("CVE-2026-31337"), compromised: prop.string("true") },
  },
  {
    id: 3,
    label: "criticalInfrastructure",
    name: "Critical Infrastructure",
    icon: "🏭",
    role: "target",
    riskScore: 90,
    description: "High-value operational-technology asset; the campaign's primary objective.",
    extra: { sector: prop.string("energy") },
  },
  {
    id: 4,
    label: "governmentAgency",
    name: "Government Agency",
    icon: "🏛️",
    role: "target",
    riskScore: 72,
    description: "Public-sector victim reached through the same compromised tool.",
    extra: { sector: prop.string("public") },
  },
  {
    id: 5,
    label: "soc",
    name: "SOC",
    icon: "🛡️",
    role: "defense",
    riskScore: 30,
    description: "Security Operations Center monitoring the estate.",
  },
  {
    id: 6,
    label: "analyst",
    name: "Security Analyst",
    icon: "🧑‍💻",
    role: "defense",
    riskScore: 25,
    description: "Human analyst employed by the SOC, investigating the critical asset.",
    extra: { clearance: prop.string("secret") },
  },
];

/** [source, target, edgePropertyId, human label, exploitCost]. */
const RELATIONSHIPS: ReadonlyArray<readonly [number, number, string, string, number]> = [
  [1, 2, "suppliesTrojan", "supplies trojan", 2.0],
  [2, 3, "deliversPayloadTo", "delivers payload to", 1.5],
  [2, 4, "deliversPayloadTo", "delivers payload to", 1.5],
  [5, 6, "employs", "employs", 0.5],
  [6, 3, "investigates", "investigates", 0.5],
];

export function buildCyberWarfare(): BuiltSample {
  const vertices: JsonlVertex[] = ENTITIES.map((e) => ({
    id: e.id,
    label: e.label,
    properties: {
      name: prop.string(e.name),
      role: prop.string(e.role),
      riskScore: prop.int32(e.riskScore),
      icon: prop.string(e.icon),
      description: prop.string(e.description),
      ...(e.extra ?? {}),
    },
  }));

  const edges: JsonlEdge[] = RELATIONSHIPS.map(([source, target, edgePropertyId, label, exploitCost], index) => ({
    id: 100 + index,
    source,
    target,
    edgePropertyId,
    label,
    properties: { exploitCost: prop.double(exploitCost) },
  }));

  return {
    jsonl: buildJsonlGraph(vertices, edges),
    entry: {
      id: "cyber-warfare",
      title: "Asymmetric Cyber Warfare",
      emoji: "🛡️",
      pitch:
        "Six entities, five directed relationships: a nation-state actor weaponizes a supply-chain tool to hit multiple targets while a SOC investigates. The full blast radius is one native traversal, not a pile of brittle joins.",
      vertexCount: vertices.length,
      edgeCount: edges.length,
      badges: ["canvas", "path", "analytics"],
      trySteps: [
        "Path: Dijkstra from the Nation State Actor to the Critical Infrastructure using edge cost 'exploitCost'. The cheapest chain runs through the compromised supply-chain tool (look ids up on the Browser screen).",
        "Subgraph: a Vertex, Edge, Vertex pattern that captures the Software Supply Chain Tool and everything it 'deliversPayloadTo' (the blast radius); recalculate after adding data.",
        "Analytics: PAGERANK, then color the canvas by the score. The compromised tool and the critical target rank highest.",
        "Query: property scan on 'riskScore' (GreaterThan, 80) for the crown-jewel assets; or color the canvas by 'role' to split adversary, targets, and defenders.",
      ],
      file: "cyber-warfare.jsonl",
      styleConfig: {
        nodeColorMode: "label",
        nodeSizeMode: "degree",
        nodeImageProperty: "icon",
        showEdgeLabels: true,
        edgeArrows: true,
      },
      indexRecipes: [],
      embedding: null,
    },
  };
}
