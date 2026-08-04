// MIT License
//
// windFarm.ts
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
 * wind-farm - "Wind Farm Fleet Integrity" (feature knowledge-demo). The sample the semantic
 * layer never had: an operational asset graph that three REAL documents are ingested into at
 * load time, so the knowledge graph the pipeline produces is joined to the domain graph and a
 * question is answered by both together.
 *
 * The asset graph is baked here like any other sample. The documents are NOT baked: the loader
 * ingests them through /document, so docling conversion, embedding, spaCy enrichment and
 * exact-match structural linking all actually run. Chunks therefore carry `mentions` edges
 * reaching BOTH the NER Entity vertices and the asset vertices below.
 *
 * Every asset tag is identifier-shaped (starts uppercase, at least 4 characters, underscore
 * separated) because that is what the ingestion identifier extractor matches and linking is
 * ordinal-exact. Technicians deliberately carry NO assetTag: prose names do not extract, so
 * people reach the graph through NER instead. That split is the demo's second lesson.
 *
 * The tags, readings and batch membership live in data/windFarmFleet.json, shared with
 * samples/documents/generate-documents.py so the documents and the graph cannot drift.
 */

import {
  buildJsonlGraph,
  prop,
  type JsonlEdge,
  type JsonlProperty,
  type JsonlVertex,
} from "../../src/lib/jsonlGraph";
import type { SampleManifestEntry } from "../../src/lib/samples";
import fleet from "./data/windFarmFleet.json";
import type { BuiltSample } from "./shared";

/** The index the documents link against; seeded after import because creation does not backfill. */
const ASSET_TAG_INDEX = "asset-tags";

/** The property holding the identifier-shaped tag, and the seed key. */
const ASSET_TAG_PROPERTY = "assetTag";

/** The turbine the three documents are written about; everything else is derived from the data. */
const SUBJECT_TURBINE = "WTG_A17";

const ICONS = {
  Site: "🌊",
  Substation: "🔌",
  GridConnection: "⚡",
  Turbine: "🌬️",
  Gearbox: "⚙️",
  CastingBatch: "🏭",
  WorkOrder: "📋",
  Technician: "🧑‍🔧",
  Standard: "📐",
} as const;

interface Fleet {
  operator: string;
  region: string;
  standard: { tag: string; title: string; alarmMmPerS: number; warningMmPerS: number; revision: string };
  grid: { tag: string; name: string };
  sites: { tag: string; name: string; waterDepthM: number; turbineCount: number }[];
  substations: { tag: string; site: string; capacityMw: number }[];
  batches: { tag: string; foundry: string; suspect: boolean; note: string }[];
  technicians: { name: string; role: string; signsDocuments: boolean }[];
  vendors: string[];
  turbines: {
    tag: string;
    site: string;
    substation: string;
    ratedMw: number;
    gearbox: {
      tag: string;
      batch: string;
      vibration: number;
      status: string;
      lastService: string;
      technician: string;
      workOrder: string;
    };
  }[];
  registerRows: string[];
}

export function buildWindFarm(): BuiltSample {
  const data = fleet as unknown as Fleet;
  const vertices: JsonlVertex[] = [];
  const edges: JsonlEdge[] = [];
  const idOf = new Map<string, number>();
  /**
   * Edge ids start here because fallen8-jsonl file ids share ONE namespace across vertex and
   * edge lines (BulkController's import session tracks them in a single set), so an edge id
   * colliding with a vertex id fails the import. The offset is load-bearing, not cosmetic.
   */
  const EDGE_ID_BASE = 10_000;

  let nextVertexId = 0;
  let nextEdgeId = EDGE_ID_BASE;

  /** Every asset vertex carries assetTag; `key` is the map handle (the tag, or a name). */
  function addVertex(
    key: string,
    label: keyof typeof ICONS,
    name: string,
    description: string,
    extra: Record<string, JsonlProperty> = {},
    assetTag?: string,
  ): number {
    const id = nextVertexId++;
    idOf.set(key, id);
    vertices.push({
      id,
      label,
      properties: {
        name: prop.string(name),
        icon: prop.string(ICONS[label]),
        description: prop.string(description),
        ...(assetTag ? { [ASSET_TAG_PROPERTY]: prop.string(assetTag) } : {}),
        ...extra,
      },
    });
    return id;
  }

  function connect(from: string, to: string, edgePropertyId: string, label: string): void {
    const source = idOf.get(from);
    const target = idOf.get(to);
    if (source === undefined || target === undefined) {
      throw new Error(`wind-farm: cannot connect '${from}' to '${to}' (unknown vertex)`);
    }
    edges.push({ id: nextEdgeId++, source, target, edgePropertyId, label });
  }

  // ---- the grid, sites and substations ----
  addVertex(
    data.grid.tag,
    "GridConnection",
    data.grid.tag,
    `${data.grid.name}: the transmission area both sites export into.`,
    {},
    data.grid.tag,
  );

  for (const site of data.sites) {
    addVertex(
      site.tag,
      "Site",
      site.tag,
      `${site.name}, an offshore site in ${data.region} operated by ${data.operator}, ` +
        `${site.turbineCount} turbines in ${site.waterDepthM} m of water.`,
      { siteName: prop.string(site.name), waterDepthM: prop.int32(site.waterDepthM) },
      site.tag,
    );
  }

  for (const substation of data.substations) {
    addVertex(
      substation.tag,
      "Substation",
      substation.tag,
      `Collector substation rated ${substation.capacityMw} MW.`,
      { capacityMw: prop.int32(substation.capacityMw) },
      substation.tag,
    );
    connect(substation.tag, substation.site, "located_at", "located at");
    connect(substation.tag, data.grid.tag, "feeds", "feeds");
  }

  // ---- the standard the documents refer to ----
  addVertex(
    data.standard.tag,
    "Standard",
    data.standard.tag,
    `${data.standard.title} (${data.standard.revision}). Alarm at ` +
      `${data.standard.alarmMmPerS} mm/s RMS, warning at ${data.standard.warningMmPerS} mm/s RMS.`,
    {
      title: prop.string(data.standard.title),
      alarmMmPerS: prop.double(data.standard.alarmMmPerS),
      warningMmPerS: prop.double(data.standard.warningMmPerS),
    },
    data.standard.tag,
  );
  for (const site of data.sites) {
    connect(data.standard.tag, site.tag, "applies_to", "applies to");
  }

  // ---- casting batches: the pivot the documents never enumerate ----
  for (const batch of data.batches) {
    addVertex(
      batch.tag,
      "CastingBatch",
      batch.tag,
      `Gearbox casting ${batch.foundry}. ${batch.note}`,
      { foundry: prop.string(batch.foundry), suspect: prop.string(String(batch.suspect)) },
      batch.tag,
    );
  }

  // ---- technicians: NO assetTag, on purpose (prose names do not extract) ----
  for (const technician of data.technicians) {
    addVertex(
      `person:${technician.name}`,
      "Technician",
      technician.name,
      `${technician.role} at ${data.operator}.`,
      { role: prop.string(technician.role) },
    );
  }

  // ---- turbines, gearboxes and the work orders that serviced them ----
  for (const turbine of data.turbines) {
    const site = data.sites.find((s) => s.tag === turbine.site);
    addVertex(
      turbine.tag,
      "Turbine",
      turbine.tag,
      `${turbine.ratedMw} MW turbine at ${site?.name ?? turbine.site}, exporting through ` +
        `${turbine.substation}.`,
      { ratedMw: prop.double(turbine.ratedMw) },
      turbine.tag,
    );
    connect(turbine.tag, turbine.site, "located_at", "located at");
    connect(turbine.tag, turbine.substation, "feeds", "feeds");

    const gbx = turbine.gearbox;
    addVertex(
      gbx.tag,
      "Gearbox",
      gbx.tag,
      `Gearbox installed in ${turbine.tag}, cast in ${gbx.batch}. Last broadband reading ` +
        `${gbx.vibration} mm/s RMS (${gbx.status}), serviced ${gbx.lastService}.`,
      {
        vibrationMmPerS: prop.double(gbx.vibration),
        status: prop.string(gbx.status),
        lastService: prop.string(gbx.lastService),
      },
      gbx.tag,
    );
    connect(turbine.tag, gbx.tag, "has_component", "has component");
    connect(gbx.tag, gbx.batch, "from_batch", "from batch");

    addVertex(
      gbx.workOrder,
      "WorkOrder",
      gbx.workOrder,
      `Service visit on ${gbx.lastService} for ${gbx.tag}, carried out by ${gbx.technician}.`,
      { performedOn: prop.string(gbx.lastService) },
      gbx.workOrder,
    );
    connect(gbx.workOrder, gbx.tag, "performed_on", "performed on");
    connect(gbx.workOrder, `person:${gbx.technician}`, "carried_out_by", "carried out by");
  }

  if (nextVertexId > EDGE_ID_BASE) {
    throw new Error(
      `wind-farm: ${nextVertexId} vertices collide with the edge id base ${EDGE_ID_BASE}; ` +
        "fallen8-jsonl file ids share one namespace, so raise the base",
    );
  }

  const suspect = data.batches.find((b) => b.suspect);
  if (!suspect) {
    throw new Error("wind-farm: windFarmFleet.json has no suspect batch, so the demo has no payoff");
  }
  const suspectMembers = data.turbines.filter((t) => t.gearbox.batch === suspect.tag);
  // DERIVED, never hardcoded: the register document lists exactly `registerRows`, and the RCA
  // deliberately refuses to enumerate the batch, so the turbines the corpus names are precisely
  // the suspect members that appear in the register. Hardcoding this set would let a change to
  // registerRows silently falsify the card and the docs while every count still looked right.
  const namedInDocuments = new Set(
    suspectMembers.filter((t) => data.registerRows.includes(t.tag)).map((t) => t.tag),
  );
  // The RCA names its subject, so the derivation above is only equivalent to "named in the
  // corpus" while the subject is also in the register. Drop it from registerRows and the one
  // turbine the documents are ABOUT would be counted as unnamed.
  if (!data.registerRows.includes(SUBJECT_TURBINE)) {
    throw new Error(
      `wind-farm: ${SUBJECT_TURBINE} is the RCA's subject but is absent from registerRows, so ` +
        "the 'named in no document' derivation would wrongly include it",
    );
  }
  const hidden = suspectMembers.filter((t) => !namedInDocuments.has(t.tag));
  if (hidden.length < 3) {
    throw new Error(
      `wind-farm: only ${hidden.length} suspect-batch turbines go unnamed by the documents; ` +
        "the blast-radius payoff needs several, so widen the batch in windFarmFleet.json",
    );
  }

  // Everything the card's text names is derived from the fleet file, so prose and data cannot
  // drift apart. SUBJECT_TURBINE is the one turbine the documents are actually about.
  const subject = data.turbines.find((t) => t.tag === SUBJECT_TURBINE);
  if (!subject) {
    throw new Error(`wind-farm: ${SUBJECT_TURBINE} is missing from windFarmFleet.json`);
  }
  const subjectGearbox = subject.gearbox.tag;
  const SUBJECT_SUBSTATION = subject.substation;
  const vendors = data.vendors;
  const signerName = (data.technicians.find((t) => t.signsDocuments) ?? data.technicians[0]).name;

  const entry: SampleManifestEntry = {
    id: "wind-farm",
    title: "Wind Farm Fleet Integrity",
    emoji: "🌬️",
    pitch:
      "Three synthetic documents (a PDF root-cause analysis with a figure, a spreadsheet " +
      "register, a markdown standard) are ingested live into an offshore asset graph. Ask why a " +
      "gearbox failed, land on the paragraph that explains it, then follow the chunk into the " +
      "fleet to find the turbines at risk that no document names.",
    vertexCount: vertices.length,
    edgeCount: edges.length,
    badges: ["knowledge", "semantic", "canvas", "path"],
    // Every claim below was checked against a real load. Nothing here asserts a retrieval
    // behaviour the shipped corpus does not actually show.
    trySteps: [
      // The three words below are quoted from the shipped corpus, so this claim stays checkable.
      'Knowledge screen, Search: ask "why did the bearing fail" in plain language. You land on ' +
        "the section that explains the mechanism without ever having to guess the words it uses " +
        "(rolling contact fatigue, spalling, Hertzian contact stress).",
      'Search again for "why is a single vibration number not enough", then switch mode to ' +
        "lexical: keyword matching lands on the WRONG section, while the default fused mode gets " +
        "it right because the dense side understands the paraphrase. That is what fusion buys.",
      // The asset side is deterministic (exact tag match); the entity side depends on the spaCy
      // model and tier, so it is described rather than enumerated.
      'Send the top hit to the canvas and expand it. Its "mentions" edges reach BOTH worlds at ' +
        `once: the real assets ${SUBJECT_TURBINE}, ${subjectGearbox} and ${data.standard.tag} by ` +
        "exact tag match, plus whichever named entities the NLP sidecar found in that same " +
        `paragraph. Open the report's opening section for the richest entity fan-out (${vendors[0]}, ` +
        `${signerName}, the North Sea).`,
      `Now search for ${hidden[0].tag}. You get confident-looking hits that never actually name ` +
        "it, because no document covers that turbine. Retrieval alone cannot answer this.",
      `Instead expand ${subjectGearbox} to ${suspect.tag} and expand the batch: ` +
        `${suspectMembers.length} gearboxes were cast in that run, and ${hidden.length} of the ` +
        `turbines carrying them (${hidden.map((t) => t.tag).join(", ")}) appear in NO document. ` +
        "The corpus explains the mechanism; the graph gives you the blast radius.",
      `Knowledge screen, Entities: one ${signerName} vertex whose mention count spans all three ` +
        "documents, because entities deduplicate per namespace. Note the same person also exists " +
        "as a Technician vertex from the asset import: resolving the two is your next graph " +
        "problem, and you have both sides of it.",
      `Path: from the chunk that explains the failure to ${SUBJECT_SUBSTATION}, to see a text ` +
        "hit and an electrical asset joined in one traversal.",
    ],
    file: "wind-farm.jsonl",
    styleConfig: {
      nodeColorMode: "label",
      nodeSizeMode: "degree",
      nodeImageProperty: "icon",
      showNodeLabels: true,
      showEdgeLabels: true,
      edgeArrows: true,
    },
    indexRecipes: [
      {
        uniqueId: ASSET_TAG_INDEX,
        pluginType: "DictionaryIndex",
        pluginOptions: {},
      },
    ],
    // No baked vectors: the vectors this sample demonstrates are the CHUNK embeddings the
    // pipeline computes at ingest, and the vector index comes from the document binding.
    embedding: null,
    indexSeeds: [{ indexId: ASSET_TAG_INDEX, propertyId: ASSET_TAG_PROPERTY }],
    linkIndexIds: [ASSET_TAG_INDEX],
    documents: [
      {
        file: "documents/nw-rca-wtg-a17.pdf",
        name: "nw-rca-wtg-a17.pdf",
        kind: "binary",
      },
      {
        file: "documents/nw-fleet-register.xlsx",
        name: "nw-fleet-register.xlsx",
        kind: "binary",
      },
      {
        file: "documents/nw-std-0417.md",
        name: "nw-std-0417.md",
        kind: "text",
        format: "markdown",
      },
    ],
  };

  return { jsonl: buildJsonlGraph(vertices, edges), entry };
}
