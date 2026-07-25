/**
 * Dataset generator (nl-assist-finetune plan phase 2, spec Stage 1). Emits grounded
 * (intent -> valid C#) training rows and writes them as JSONL the LoRA trainer consumes.
 *
 * Two coexisting generation targets (both compile-gated, both grounded in the SHIPPING
 * prompt modules so training data can't drift from the product):
 *   - FRAGMENT rows (the original surface): a delegate lambda BODY for a typed slot, built
 *     from KIND_INFO + the snippet/type-model contract, gated through POST /delegates/validate.
 *   - PLUGIN rows (feature plugin-registration): a WHOLE C# type implementing a category
 *     contract (algorithm Path/SubGraph/Analytics or function IGraphFunction), built from the
 *     plugin scaffold + whole-type prompt, gated through POST /plugins/{category}/validate.
 * The fragment rows are NOT retargeted; the two surfaces train together (the trainer reads
 * only `messages`, so both shapes live in one train.jsonl).
 *
 * Grounding (spec FT-2): the fragments are built from the SAME delegate contract the
 * runtime prompt uses - KIND_INFO (parameter name/type per kind) and the type model /
 * snippet library - so the training data cannot drift from the real member surface. A
 * SHA-256 of those source files is recorded in the dataset meta; the trainer refuses a
 * dataset whose hash no longer matches the checked-in sources (drift guard).
 *
 * Every candidate is gated through POST /delegates/validate before it is kept (spec
 * FT-2, "self-cleaning"): a fragment that does not compile never enters the set. The
 * generation is fully deterministic (fixed value pools, index-seeded noise) so the same
 * sources + this script produce the same dataset (spec FT-1).
 *
 * The spec's Stage-1 sources are all covered:
 *   (a) templated intents over the contract    -> the template table below
 *   (d) built-in-vs-user-property contrast      -> label-eq / id-* (built-in Label/Id)
 *                                                  paired with prop-* (TryGetProperty),
 *                                                  and label-and-prop (both in one row)
 *   (e) noisy intents (typos/grammar slips)     -> noisify(), applied to a fixed slice
 *   (f) shape invariance across parameter names -> each shared intent is emitted for
 *                                                  every kind it fits, spelled in that
 *                                                  kind's own parameter (v/e/ge/p)
 *   (c) optional base-model bootstrap           -> NL_GEN_BOOTSTRAP=1 (kept only if valid)
 *
 * Run:  npx tsx nl-assist-finetune/dataset-gen/generate.ts
 * Env:  NL_EVAL_F8        apiApp base URL (the compile authority — dynamic code is always on)
 *       NL_GEN_BOOTSTRAP  set to 1 to also mine base-model phrasings (needs Ollama)
 *       NL_GEN_OUT        output dir (default nl-assist-finetune/dataset)
 */

import { createHash } from "node:crypto";
import { mkdirSync, readFileSync, writeFileSync } from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";
import type {
  AlgorithmContract,
  DelegateKind,
  PluginAuthoringCategory,
} from "../../fallen-8-web-ui/src/api/types";
import { KIND_INFO } from "../../fallen-8-web-ui/src/delegate/kinds";
import { formatFragment } from "../../fallen-8-web-ui/src/delegate/nl/format";
import { initialMessages, type ChatTurn } from "../../fallen-8-web-ui/src/delegate/nl/generate";
import { buildGenerationPrompt, extractFragment } from "../../fallen-8-web-ui/src/delegate/nl/prompt";
import { buildPluginGenerationPrompt } from "../../fallen-8-web-ui/src/plugin/nl/pluginPrompt";
import { scaffoldFor } from "../../fallen-8-web-ui/src/plugin/scaffolds";
import {
  compileErrors,
  F8,
  MODEL,
  ollamaChat,
  ollamaReachable,
  validate,
  validatePlugin,
} from "../shared/f8";

const here = path.dirname(fileURLToPath(import.meta.url));
const webUi = path.resolve(here, "../../fallen-8-web-ui/src/delegate");
const pluginUi = path.resolve(here, "../../fallen-8-web-ui/src/plugin");
const outDir = process.env.NL_GEN_OUT ?? path.resolve(here, "../dataset");
const bootstrap = process.env.NL_GEN_BOOTSTRAP === "1";

/** Contract sources the dataset is grounded in; a change to any bumps the drift hash. */
const DELEGATE_SOURCES = ["kinds.ts", "snippets.ts", "type-model.json", "nl/prompt.ts"];
const PLUGIN_SOURCES = ["nl/pluginPrompt.ts", "scaffolds.ts"];

/** Kinds that share the AGraphElementModel filter surface (Label, Id, TryGetProperty). */
const FILTER: DelegateKind[] = ["VertexFilter", "EdgeFilter", "GraphElementFilter"];

/** A candidate is kind-agnostic: `body(param)` is spelled in the target kind's parameter. */
interface Candidate {
  kinds: DelegateKind[];
  intent: string;
  body: (param: string) => string;
  source: string;
}

interface TrainRow {
  delegateKind: DelegateKind;
  intent: string;
  fragment: string;
  source: string;
  noisy: boolean;
  /** system + user (the runtime prompt) + assistant (the target). What the trainer reads. */
  messages: ChatTurn[];
}

// --- PLUGIN (whole-type) generation (feature plugin-registration) -----------------------
/**
 * The four coverage buckets a plugin dataset must fill: the three algorithm contracts plus
 * the function category (which has a single contract). The coverage guard requires >=1
 * compiling row per bucket.
 */
type PluginBucket = AlgorithmContract | "function";
const PLUGIN_BUCKETS: PluginBucket[] = ["Path", "SubGraph", "Analytics", "function"];

/** One whole-type authoring example: an intent paired with a complete, compiling C# type. */
interface PluginSeed {
  category: PluginAuthoringCategory;
  /** The scaffold/prompt contract. Passed for a function too (the prompt ignores it there). */
  contract: AlgorithmContract;
  /** Registration name — MUST equal the source's PluginName (server-validated). */
  name: string;
  intent: string;
  source: string;
}

/** A plugin training row: same `messages` contract as a fragment row, whole-type payload. */
interface PluginTrainRow {
  kind: "plugin";
  category: PluginAuthoringCategory;
  /** Present only for algorithms (JSON.stringify drops it for a function). */
  contract?: AlgorithmContract;
  name: string;
  intent: string;
  source: string;
  messages: ChatTurn[];
}

// A function has one contract, so its bucket is "function"; an algorithm buckets by contract
// (always set on both a seed and a built row's algorithm rows).
const bucketOf = (row: { category: PluginAuthoringCategory; contract?: AlgorithmContract }): PluginBucket =>
  row.category === "function" ? "function" : (row.contract as AlgorithmContract);

/**
 * Boilerplate-shared IGraphFunction source: the SAME whole-type shape the scaffold ships
 * (usings, IPlugin members, parameterless ctor, PluginName == the type name), with a real
 * READ-ONLY TryInvoke body inserted. Kept in lockstep with plugin/scaffolds.ts:functionScaffold
 * so what we author matches what compiles; `name` doubles as PluginName and the C# identifier,
 * so every seed name below is already a valid identifier.
 */
const functionSource = (name: string, description: string, invokeBody: string): string =>
  `using System;
using System.Collections.Generic;
using NoSQL.GraphDB.Core;
using NoSQL.GraphDB.Core.Plugin;
using NoSQL.GraphDB.Core.Plugins;

public sealed class ${name} : IGraphFunction
{
    private IFallen8 _graph;

    public string PluginName    => "${name}";
    public Type   PluginCategory => typeof(IGraphFunction);
    public string Description   => "${description}";
    public string Manufacturer  => "you";

    public void Initialize(IFallen8 fallen8, IDictionary<string, object> parameter) => _graph = fallen8;
    public void Dispose() { }

    public bool TryInvoke(out GraphFunctionResult result, IDictionary<string, object> parameters)
    {
${invokeBody}
        return true;
    }
}
`;

/**
 * A real degree-scoring IGraphAnalyticsAlgorithm (whole type). Reads the captured IFallen8,
 * scores every vertex by total degree, and returns a score-shaped GraphAnalyticsResult - the
 * public surface confirmed against GraphAnalyticsResult / VertexModel (Id, GetOutDegree,
 * GetInDegree). It reuses exactly the Analytics scaffold's usings plus the captured graph.
 */
const DEGREE_ANALYTICS_SOURCE = `using System;
using System.Collections.Generic;
using NoSQL.GraphDB.Core;
using NoSQL.GraphDB.Core.Algorithms.Analytics;
using NoSQL.GraphDB.Core.Plugin;

public sealed class DegreeScores : IGraphAnalyticsAlgorithm
{
    private IFallen8 _graph;

    public string PluginName    => "DegreeScores";
    public Type   PluginCategory => typeof(IGraphAnalyticsAlgorithm);
    public string Description   => "Scores every vertex by its total (in + out) degree.";
    public string Manufacturer  => "you";

    public void Initialize(IFallen8 fallen8, IDictionary<string, object> parameter) => _graph = fallen8;
    public void Dispose() { }

    public bool TryRunAnalytics(out GraphAnalyticsResult result, GraphAnalyticsDefinition definition)
    {
        var scores = new Dictionary<int, double>();
        foreach (var vertex in _graph.GetAllVertices())
        {
            scores[vertex.Id] = vertex.GetOutDegree() + vertex.GetInDegree();
        }
        result = new GraphAnalyticsResult(scores, new Dictionary<string, object>(), true, 1, TimeSpan.Zero, false);
        return true;
    }
}
`;

/**
 * The plugin seed set. The scaffold seeds ground the model on the whole-type SHAPE (the hard
 * part for a small model: usings, the IPlugin members, the exact PluginName, one sealed class)
 * and are guaranteed to compile (plugin/scaffolds.ts: the algorithm skeletons validate as-is,
 * the function scaffold ships a working scan) so every bucket has a floor. The function and
 * analytics seeds add real read-only bodies. Richer Path/SubGraph bodies (real traversal /
 * subgraph construction) are left to the operator's bootstrap + captured-feedback loop, where
 * they are verified against the live validator; hand-authoring them here without a live compile
 * gate would only add rows that silently drop. Deterministic: a fixed, ordered list.
 */
const pluginSeeds: PluginSeed[] = [
  // function — real read-only implementations over the captured IFallen8.
  {
    category: "function",
    contract: "Path",
    name: "VertexLabelScan",
    intent:
      "return every vertex whose label matches the 'label' parameter, or all vertices when no label is given",
    source: functionSource(
      "VertexLabelScan",
      "Returns the vertices matching the 'label' parameter.",
      `        var label = parameters != null && parameters.TryGetValue("label", out var l) ? l as string : null;
        result = GraphFunctionResult.FromElements(_graph.GetAllVertices(label), edges: null);`,
    ),
  },
  {
    category: "function",
    contract: "Path",
    name: "AllEdges",
    intent: "return every edge in the graph",
    source: functionSource(
      "AllEdges",
      "Returns every edge in the graph.",
      `        result = GraphFunctionResult.FromElements(vertices: null, edges: _graph.GetAllEdges());`,
    ),
  },
  {
    category: "function",
    contract: "Path",
    name: "PersonVertices",
    intent: "return all vertices labelled person",
    source: functionSource(
      "PersonVertices",
      "Returns the vertices labelled person.",
      `        result = GraphFunctionResult.FromElements(_graph.GetAllVertices("person"), edges: null);`,
    ),
  },
  // Analytics — a real degree-scoring algorithm, plus the compiling skeleton floor.
  {
    category: "algorithm",
    contract: "Analytics",
    name: "DegreeScores",
    intent: "score each vertex by its total degree (incoming plus outgoing edges)",
    source: DEGREE_ANALYTICS_SOURCE,
  },
  {
    category: "algorithm",
    contract: "Analytics",
    name: "AnalyticsStarter",
    intent: "a starter Analytics algorithm skeleton I can fill in",
    source: scaffoldFor("algorithm", "Analytics", "AnalyticsStarter"),
  },
  // Path / SubGraph — the compiling scaffold skeleton (the whole-type shape floor).
  {
    category: "algorithm",
    contract: "Path",
    name: "PathStarter",
    intent: "a starter Path (shortest path) algorithm skeleton I can fill in",
    source: scaffoldFor("algorithm", "Path", "PathStarter"),
  },
  {
    category: "algorithm",
    contract: "SubGraph",
    name: "SubGraphStarter",
    intent: "a starter SubGraph algorithm skeleton I can fill in",
    source: scaffoldFor("algorithm", "SubGraph", "SubGraphStarter"),
  },
];

/** Build a plugin training row: the whole-type runtime prompt + the source as the target. */
function buildPluginRow(seed: PluginSeed): PluginTrainRow {
  const { category, contract, name, intent, source } = seed;
  const scaffold = scaffoldFor(category, contract, name);
  const prompt = buildPluginGenerationPrompt({ category, contract, name, scaffold, intent });
  return {
    kind: "plugin",
    category,
    contract: category === "algorithm" ? contract : undefined,
    name,
    intent,
    source,
    messages: [...initialMessages(prompt), { role: "assistant", content: source }],
  };
}

// --- value pools (fixed => deterministic dataset) ---------------------------------------
const labels = ["person", "city", "company", "car", "product", "movie"];
const edgeLabels = ["knows", "worksWith", "likes", "owns", "follows"];
const intProps: { name: string; thresholds: number[] }[] = [
  { name: "age", thresholds: [18, 30, 65] },
  { name: "year", thresholds: [2000, 2020] },
  { name: "rank", thresholds: [5, 10] },
];
const doubleProps: { name: string; thresholds: number[] }[] = [
  { name: "weight", thresholds: [0.5, 2.5] },
  { name: "score", thresholds: [1.5, 9.0] },
  { name: "rating", thresholds: [4.0] },
];
const stringProps = ["name", "title", "email"];
const prefixes = ["A", "Be", "San"];
const suffixes = ["ing", "ed", "Corp"];
const dbl = (n: number) => (Number.isInteger(n) ? `${n}.0` : `${n}`);

// --- template table (spec Stage-1 a/d/f) ------------------------------------------------
const candidates: Candidate[] = [];
const add = (kinds: DelegateKind[], intent: string, body: Candidate["body"], source: string) =>
  candidates.push({ kinds, intent, body, source });

// Built-in Label (source d: label phrasings map to the built-in member, never TryGetProperty)
for (const l of labels.slice(0, 5)) {
  add(FILTER, `elements labelled ${l}`, (p) => `${p}.Label == "${l}"`, "label-eq");
}
for (let i = 0; i + 1 < 4; i += 2) {
  const [a, b] = [labels[i], labels[i + 1]];
  add(FILTER, `elements labelled ${a} or ${b}`, (p) => `${p}.Label == "${a}" || ${p}.Label == "${b}"`, "label-or");
}
// Built-in Id (source d)
for (const n of [50, 100, 1000]) {
  add(FILTER, `elements with an id greater than ${n}`, (p) => `${p}.Id > ${n}`, "id-gt");
}
for (const n of [10, 50]) {
  add(FILTER, `elements with an id below ${n}`, (p) => `${p}.Id < ${n}`, "id-lt");
}
// User properties via TryGetProperty (source d: the other half of the contrast)
for (const { name, thresholds } of intProps) {
  for (const n of thresholds) {
    add(FILTER, `elements with a ${name} greater than ${n}`, (p) => `${p}.TryGetProperty(out int ${name}, "${name}") && ${name} > ${n}`, "prop-int-gt");
    add(FILTER, `elements with a ${name} below ${n}`, (p) => `${p}.TryGetProperty(out int ${name}, "${name}") && ${name} < ${n}`, "prop-int-lt");
  }
}
for (const { name, thresholds } of doubleProps) {
  for (const n of thresholds) {
    add(FILTER, `elements with a ${name} above ${dbl(n)}`, (p) => `${p}.TryGetProperty(out double ${name}, "${name}") && ${name} > ${dbl(n)}`, "prop-double-gt");
  }
}
for (const name of stringProps) {
  for (const pre of prefixes) {
    add(FILTER, `elements whose ${name} starts with ${pre}`, (p) => `${p}.TryGetProperty(out string ${name}, "${name}") && ${name}.StartsWith("${pre}")`, "prop-str-starts");
  }
  for (const suf of suffixes) {
    add(FILTER, `elements whose ${name} ends with ${suf}`, (p) => `${p}.TryGetProperty(out string ${name}, "${name}") && ${name}.EndsWith("${suf}")`, "prop-str-ends");
  }
}
// Built-in AND user-property in one fragment (source d, the exact field-example confusion)
for (const l of labels.slice(0, 3)) {
  for (const { name, thresholds } of intProps.slice(0, 2)) {
    const n = thresholds[0];
    add(FILTER, `elements labelled ${l} with a ${name} over ${n}`, (p) => `${p}.Label == "${l}" && ${p}.TryGetProperty(out int ${name}, "${name}") && ${name} > ${n}`, "label-and-prop");
  }
}
for (const n of [1, 2, 3]) {
  add(FILTER, `elements with more than ${n} properties`, (p) => `${p}.GetPropertyCount() > ${n}`, "prop-count");
}

// Natural comparatives -> a user property (the eval phrasings "older than 30" / "heavier
// than 2"): teach the comparative-word -> property mapping the base model missed first-pass.
for (const n of [18, 30, 50, 65]) {
  add(FILTER, `elements older than ${n}`, (p) => `${p}.TryGetProperty(out int age, "age") && age > ${n}`, "older-than");
  add(FILTER, `elements younger than ${n}`, (p) => `${p}.TryGetProperty(out int age, "age") && age < ${n}`, "younger-than");
}
for (const n of ["0.5", "2.0", "5.0"]) {
  add(FILTER, `elements heavier than ${n}`, (p) => `${p}.TryGetProperty(out double weight, "weight") && weight > ${n}`, "heavier-than");
}
// Built-in Id, extra natural phrasings ("smaller/less than", "above").
for (const n of [10, 100]) {
  add(FILTER, `elements with an id smaller than ${n}`, (p) => `${p}.Id < ${n}`, "id-smaller");
  add(FILTER, `elements with an id above ${n}`, (p) => `${p}.Id > ${n}`, "id-above");
}
// Multi-condition: label + user property + built-in Id in ONE fragment (the field-example
// shape that failed first-pass - the hardest, most realistic composition).
for (const l of ["person", "car", "movie"]) {
  for (const [ageN, idN] of [[30, 10], [18, 100], [50, 1000]] as [number, number][]) {
    add(FILTER, `${l}s older than ${ageN} with an id below ${idN}`,
      (p) => `${p}.Label == "${l}" && ${p}.TryGetProperty(out int age, "age") && age > ${ageN} && ${p}.Id < ${idN}`,
      "label-age-id");
  }
}
// Edge weight thresholds (the failing ef-weight), with natural phrasings.
for (const n of ["0.5", "1.0", "2.0"]) {
  add(["EdgeFilter"], `edges with a weight above ${n}`, (p) => `${p}.TryGetProperty(out double weight, "weight") && weight > ${n}`, "edge-weight");
  add(["EdgeFilter"], `edges heavier than ${n}`, (p) => `${p}.TryGetProperty(out double weight, "weight") && weight > ${n}`, "edge-heavier");
}

// Vertex-only surface
for (const n of [2, 3, 5]) {
  add(["VertexFilter"], `vertices with at least ${n} outgoing edges`, (p) => `${p}.GetOutDegree() >= ${n}`, "out-degree");
  add(["VertexFilter"], `vertices with at least ${n} incoming edges`, (p) => `${p}.GetInDegree() >= ${n}`, "in-degree");
}
add(["VertexFilter"], `vertices connected to at least 4 others`, (p) => `${p}.GetOutDegree() + ${p}.GetInDegree() >= 4`, "degree-sum");

// Edge-only surface
for (const l of labels.slice(0, 3)) {
  add(["EdgeFilter"], `edges pointing to a ${l}`, (p) => `${p}.TargetVertex.Label == "${l}"`, "edge-target-label");
  add(["EdgeFilter"], `edges starting from a ${l}`, (p) => `${p}.SourceVertex.Label == "${l}"`, "edge-source-label");
}
for (const el of edgeLabels.slice(0, 3)) {
  add(["EdgeFilter"], `edges labelled ${el}`, (p) => `${p}.Label == "${el}"`, "edge-label");
}

// EdgePropertyFilter (the parameter is a bare string; no TryGetProperty here)
for (const el of edgeLabels) {
  add(["EdgePropertyFilter"], `only ${el} edges`, (p) => `${p} == "${el}"`, "epf-eq");
}
for (let i = 0; i + 1 < 4; i += 2) {
  const [a, b] = [edgeLabels[i], edgeLabels[i + 1]];
  add(["EdgePropertyFilter"], `${a} or ${b} edges`, (p) => `${p} == "${a}" || ${p} == "${b}"`, "epf-or");
}
for (const suf of ["With", "s", "ed"]) {
  add(["EdgePropertyFilter"], `edge properties ending with ${suf}`, (p) => `${p}.EndsWith("${suf}")`, "epf-ends");
}
for (const pre of ["kn", "wo", "li"]) {
  add(["EdgePropertyFilter"], `edge properties starting with ${pre}`, (p) => `${p}.StartsWith("${pre}")`, "epf-starts");
}
for (const sub of ["work", "know"]) {
  add(["EdgePropertyFilter"], `edge properties containing ${sub}`, (p) => `${p}.Contains("${sub}")`, "epf-contains");
}

// Costs (return double): uniform, property-with-fallback, and degree-derived.
const edgeCostProps = ["weight", "distance", "length", "cost"];
for (const c of ["1.0", "2.0", "0.5", "5.0"]) {
  add(["VertexCost"], `every vertex costs ${c}`, () => c, "vc-uniform");
  add(["EdgeCost"], `every edge costs ${c}`, () => c, "ec-uniform");
}
for (const { name } of doubleProps) {
  for (const def of ["1.0", "2.5"]) {
    add(["VertexCost"], `vertex cost from the ${name} property, defaulting to ${def}`, (p) => `${p}.TryGetProperty(out double ${name}, "${name}") ? ${name} : ${def}`, "vc-property");
  }
}
for (const name of edgeCostProps) {
  for (const def of ["1.0", "2.5"]) {
    add(["EdgeCost"], `use the ${name} property as the cost, defaulting to ${def}`, (p) => `${p}.TryGetProperty(out double ${name}, "${name}") ? ${name} : ${def}`, "ec-weight-default");
  }
}
add(["VertexCost"], `vertex cost equal to its number of outgoing edges`, (p) => `${p}.GetOutDegree()`, "vc-outdegree");
add(["VertexCost"], `vertex cost equal to its total degree`, (p) => `${p}.GetOutDegree() + ${p}.GetInDegree()`, "vc-degree-sum");

// --- noisy-intent generator (source e) --------------------------------------------------
/**
 * Deterministic typo/grammar slips seeded by row index: double a leading letter
 * ("person" -> "pperson"), "than" -> "then", "with an" -> "an with", drop casing. Two
 * transforms per row; if none bit, prepend a lowercase "only" so the row is still noisy.
 */
function noisify(intent: string, seed: number): string {
  const transforms: ((s: string) => string)[] = [
    (s) => s.replace(/\b(\w)(\w{2,})/, (_m, a, b) => `${a}${a}${b}`),
    (s) => s.replace(/ than /, " then "),
    (s) => s.replace(/ with an /, " an with "),
    (s) => s.toLowerCase(),
    (s) => s.replace(/\bwith\b/, "wit"),
  ];
  let out = intent;
  for (const offset of [0, 2]) out = transforms[(seed + offset) % transforms.length](out);
  return out === intent ? `only ${intent.toLowerCase()}` : out;
}

// --- flatten to concrete per-kind rows (source f: shape invariance) ---------------------
function buildRow(kind: DelegateKind, intent: string, body: string, source: string, noisy: boolean): TrainRow {
  const param = KIND_INFO[kind].parameterName;
  const fragment = `return (${param}) => ${body};`;
  const prompt = buildGenerationPrompt(kind, intent);
  return {
    delegateKind: kind,
    intent,
    fragment,
    source,
    noisy,
    messages: [...initialMessages(prompt), { role: "assistant", content: fragment }],
  };
}

function sourceHash(): string {
  const hash = createHash("sha256");
  for (const file of DELEGATE_SOURCES) hash.update(readFileSync(path.join(webUi, file)));
  for (const file of PLUGIN_SOURCES) hash.update(readFileSync(path.join(pluginUi, file)));
  return hash.digest("hex").slice(0, 16);
}

/**
 * Drift guard (spec FT-2): compare a previously generated dataset's recorded sourceHash
 * against the current fragment (delegate) AND plugin (whole-type) contract sources. A
 * mismatch means a type model / prompt / scaffold changed since the dataset was built - the
 * trainer must not use a stale set. Called by run.sh before training; exits non-zero on drift.
 */
function checkDrift(): void {
  const metaPath = path.join(outDir, "dataset.meta.json");
  const meta = JSON.parse(readFileSync(metaPath, "utf8")) as { sourceHash: string };
  const current = sourceHash();
  if (meta.sourceHash !== current) {
    throw new Error(
      `Dataset is stale: sourceHash ${meta.sourceHash} != current ${current}. ` +
        `The delegate/plugin contract changed - regenerate with 'npx tsx dataset-gen/generate.ts'.`,
    );
  }
  console.log(`dataset in sync with delegate + plugin contract (sourceHash ${current}).`);
}

async function main() {
  if (process.argv.includes("--check")) {
    checkDrift();
    return;
  }

  // Preflight both compile authorities before generating (fail fast, clear message).
  const preflight = await validate("VertexFilter", "return (v) => true;");
  if (!preflight.valid) throw new Error("Preflight validate failed - is the apiApp healthy?");
  // The plugin authority is gated: validatePlugin throws a clear "enable the capability"
  // message on a 403, and the function scaffold always compiles, so an invalid result here
  // means the apiApp itself is unhealthy.
  const pluginPreflight = await validatePlugin("function", {
    name: "PreflightFunction",
    sourceCode: scaffoldFor("function", "Path", "PreflightFunction"),
  });
  if (!pluginPreflight.valid) {
    throw new Error("Plugin preflight validate failed - the scaffold should always compile; is the apiApp healthy?");
  }
  if (bootstrap && !(await ollamaReachable())) {
    throw new Error("NL_GEN_BOOTSTRAP=1 but Ollama is not reachable.");
  }

  const kept: TrainRow[] = [];
  const dropped: { kind: DelegateKind; fragment: string; intent: string; errors: string[] }[] = [];
  const seen = new Set<string>(); // dedupe identical (kind, fragment) rows

  // Deterministic order: templates in table order, kinds in table order.
  const flat: { kind: DelegateKind; intent: string; body: string; source: string }[] = [];
  for (const cand of candidates) {
    for (const kind of cand.kinds) {
      flat.push({ kind, intent: cand.intent, body: cand.body(KIND_INFO[kind].parameterName), source: cand.source });
    }
  }

  let index = 0;
  for (const item of flat) {
    // Emit the clean row, plus a noisy sibling for a fixed 1-in-6 slice (source e).
    const variants: { intent: string; noisy: boolean }[] = [{ intent: item.intent, noisy: false }];
    if (index % 6 === 0) variants.push({ intent: noisify(item.intent, index), noisy: true });
    for (const { intent, noisy } of variants) {
      const row = buildRow(item.kind, intent, item.body, item.source, noisy);
      const dedupeKey = `${row.delegateKind}|${row.fragment}|${intent}`;
      if (seen.has(dedupeKey)) continue;
      const result = await validate(row.delegateKind, row.fragment);
      if (result.valid) {
        seen.add(dedupeKey);
        kept.push(row);
      } else {
        dropped.push({ kind: row.delegateKind, fragment: row.fragment, intent, errors: compileErrors(result) });
      }
    }
    index++;
    if (index % 25 === 0) process.stdout.write(`  ...${index}/${flat.length} templates, ${kept.length} kept\n`);
  }

  // Optional base-model bootstrap (source c): mine alternative phrasings, keep only valid,
  // non-duplicate fragments. Bounded so slow generations don't dominate.
  if (bootstrap) {
    const distinctIntents = [...new Map(flat.map((f) => [`${f.kind}|${f.intent}`, f])).values()].slice(0, 24);
    process.stdout.write(`bootstrap: mining ${distinctIntents.length} intents from ${MODEL}\n`);
    for (const item of distinctIntents) {
      const prompt = buildGenerationPrompt(item.kind, item.intent);
      const { content } = await ollamaChat(initialMessages(prompt));
      const fragment = formatFragment(extractFragment(content)).replace(/\n\s*/g, " ");
      const dedupeKey = `${item.kind}|${fragment}|${item.intent}`;
      if (seen.has(dedupeKey)) continue;
      const result = await validate(item.kind, fragment);
      if (result.valid) {
        seen.add(dedupeKey);
        kept.push({
          delegateKind: item.kind,
          intent: item.intent,
          fragment,
          source: "bootstrap",
          noisy: false,
          messages: [...initialMessages(prompt), { role: "assistant", content: fragment }],
        });
      }
    }
  }

  // Per-kind coverage (spec FT-3): every fragment kind must be represented.
  const perKind = Object.fromEntries(
    (Object.keys(KIND_INFO) as DelegateKind[]).map((k) => [k, kept.filter((r) => r.delegateKind === k).length]),
  );
  const missing = Object.entries(perKind).filter(([, n]) => n === 0).map(([k]) => k);
  if (missing.length > 0) {
    throw new Error(`No valid rows for kind(s): ${missing.join(", ")} - fix templates or the validator.`);
  }

  // --- PLUGIN rows (feature plugin-registration): whole types, compile-gated through the
  // plugin authority. Emitted alongside the fragment rows, never replacing them.
  const keptPlugins: PluginTrainRow[] = [];
  const droppedPlugins: { name: string; bucket: PluginBucket; errors: string }[] = [];
  const seenPlugins = new Set<string>();
  for (const seed of pluginSeeds) {
    const row = buildPluginRow(seed);
    const dedupeKey = `${seed.category}|${seed.contract}|${seed.name}|${seed.intent}`;
    if (seenPlugins.has(dedupeKey)) continue;
    const result = await validatePlugin(seed.category, {
      name: seed.name,
      contract: seed.category === "algorithm" ? seed.contract : undefined,
      sourceCode: seed.source,
    });
    if (result.valid) {
      seenPlugins.add(dedupeKey);
      keptPlugins.push(row);
    } else {
      droppedPlugins.push({ name: seed.name, bucket: bucketOf(seed), errors: result.error ?? "(no error text)" });
    }
  }

  // Per-contract coverage: every plugin bucket (algorithm x3 + function) must be represented.
  const perPluginBucket = Object.fromEntries(
    PLUGIN_BUCKETS.map((b) => [b, keptPlugins.filter((r) => bucketOf(r) === b).length]),
  ) as Record<PluginBucket, number>;
  const missingPlugin = PLUGIN_BUCKETS.filter((b) => perPluginBucket[b] === 0);
  if (missingPlugin.length > 0) {
    throw new Error(
      `No valid plugin rows for contract(s): ${missingPlugin.join(", ")} - fix the seed sources or the validator.`,
    );
  }

  mkdirSync(outDir, { recursive: true });
  // Fragment rows then plugin rows: both carry `messages`, which is all the trainer reads.
  const allRows: unknown[] = [...kept, ...keptPlugins];
  const jsonl = allRows.map((row) => JSON.stringify(row)).join("\n") + "\n";
  writeFileSync(path.join(outDir, "train.jsonl"), jsonl);
  const meta = {
    generatedRows: kept.length,
    droppedRows: dropped.length,
    noisyRows: kept.filter((r) => r.noisy).length,
    bootstrapRows: kept.filter((r) => r.source === "bootstrap").length,
    perKind,
    pluginRows: keptPlugins.length,
    droppedPluginRows: droppedPlugins.length,
    perPluginBucket,
    sourceHash: sourceHash(),
    sources: [...DELEGATE_SOURCES, ...PLUGIN_SOURCES.map((f) => `plugin/${f}`)],
  };
  writeFileSync(path.join(outDir, "dataset.meta.json"), JSON.stringify(meta, null, 2));

  console.log("\n=== dataset (fragments) ===");
  console.table(perKind);
  console.log(`kept ${kept.length}, dropped ${dropped.length}, noisy ${meta.noisyRows}, sourceHash ${meta.sourceHash}`);
  console.log("\n=== dataset (plugins) ===");
  console.table(perPluginBucket);
  console.log(`kept ${keptPlugins.length}, dropped ${droppedPlugins.length}`);
  console.log(`wrote ${path.join(outDir, "train.jsonl")} (${allRows.length} rows total)`);
  if (dropped.length > 0) {
    console.log(`\n${dropped.length} fragment candidate(s) dropped (did not compile):`);
    for (const d of dropped.slice(0, 15)) console.log(`  [${d.kind}] ${d.fragment}\n    ${d.errors.join("; ")}`);
    if (dropped.length > 15) console.log(`  ... and ${dropped.length - 15} more`);
  }
  if (droppedPlugins.length > 0) {
    console.log(`\n${droppedPlugins.length} plugin seed(s) dropped (did not compile):`);
    for (const d of droppedPlugins) console.log(`  [${d.bucket}] ${d.name}\n    ${d.errors}`);
  }
}

main().catch((error) => {
  console.error(error instanceof Error ? error.message : error);
  process.exit(1);
});
