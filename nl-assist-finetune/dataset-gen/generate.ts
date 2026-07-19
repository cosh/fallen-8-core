/**
 * Dataset generator (nl-assist-finetune plan phase 2, spec Stage 1). Emits grounded
 * (intent -> valid C# fragment) training rows for every delegate kind and writes them
 * as JSONL the LoRA trainer consumes.
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
 * Env:  NL_EVAL_F8        apiApp base URL (must run with EnableDynamicCodeExecution=true)
 *       NL_GEN_BOOTSTRAP  set to 1 to also mine base-model phrasings (needs Ollama)
 *       NL_GEN_OUT        output dir (default nl-assist-finetune/dataset)
 */

import { createHash } from "node:crypto";
import { mkdirSync, readFileSync, writeFileSync } from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";
import type { DelegateKind } from "../../fallen-8-web-ui/src/api/types";
import { KIND_INFO } from "../../fallen-8-web-ui/src/delegate/kinds";
import { formatFragment } from "../../fallen-8-web-ui/src/delegate/nl/format";
import { initialMessages, type ChatTurn } from "../../fallen-8-web-ui/src/delegate/nl/generate";
import { buildGenerationPrompt, extractFragment } from "../../fallen-8-web-ui/src/delegate/nl/prompt";
import { compileErrors, F8, MODEL, ollamaChat, ollamaReachable, validate } from "../shared/f8";

const here = path.dirname(fileURLToPath(import.meta.url));
const webUi = path.resolve(here, "../../fallen-8-web-ui/src/delegate");
const outDir = process.env.NL_GEN_OUT ?? path.resolve(here, "../dataset");
const bootstrap = process.env.NL_GEN_BOOTSTRAP === "1";

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
  for (const file of ["kinds.ts", "snippets.ts", "type-model.json", "nl/prompt.ts"]) {
    hash.update(readFileSync(path.join(webUi, file)));
  }
  return hash.digest("hex").slice(0, 16);
}

/**
 * Drift guard (spec FT-2): compare a previously generated dataset's recorded sourceHash
 * against the current delegate-contract sources. A mismatch means the type model/prompt
 * changed since the dataset was built - the trainer must not use a stale set. Called by
 * run.sh before training; exits non-zero on drift.
 */
function checkDrift(): void {
  const metaPath = path.join(outDir, "dataset.meta.json");
  const meta = JSON.parse(readFileSync(metaPath, "utf8")) as { sourceHash: string };
  const current = sourceHash();
  if (meta.sourceHash !== current) {
    throw new Error(
      `Dataset is stale: sourceHash ${meta.sourceHash} != current ${current}. ` +
        `The delegate contract changed - regenerate with 'npx tsx dataset-gen/generate.ts'.`,
    );
  }
  console.log(`dataset in sync with delegate contract (sourceHash ${current}).`);
}

async function main() {
  if (process.argv.includes("--check")) {
    checkDrift();
    return;
  }

  // Preflight the compile authority before generating (fail fast, clear message).
  const preflight = await validate("VertexFilter", "return (v) => true;");
  if (!preflight.valid) throw new Error("Preflight validate failed - is the apiApp healthy?");
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

  // Per-kind coverage (spec FT-3): every kind must be represented.
  const perKind = Object.fromEntries(
    (Object.keys(KIND_INFO) as DelegateKind[]).map((k) => [k, kept.filter((r) => r.delegateKind === k).length]),
  );
  const missing = Object.entries(perKind).filter(([, n]) => n === 0).map(([k]) => k);
  if (missing.length > 0) {
    throw new Error(`No valid rows for kind(s): ${missing.join(", ")} - fix templates or the validator.`);
  }

  mkdirSync(outDir, { recursive: true });
  const jsonl = kept.map((row) => JSON.stringify(row)).join("\n") + "\n";
  writeFileSync(path.join(outDir, "train.jsonl"), jsonl);
  const meta = {
    generatedRows: kept.length,
    droppedRows: dropped.length,
    noisyRows: kept.filter((r) => r.noisy).length,
    bootstrapRows: kept.filter((r) => r.source === "bootstrap").length,
    perKind,
    sourceHash: sourceHash(),
    sources: ["kinds.ts", "snippets.ts", "type-model.json", "nl/prompt.ts"],
  };
  writeFileSync(path.join(outDir, "dataset.meta.json"), JSON.stringify(meta, null, 2));

  console.log("\n=== dataset ===");
  console.table(perKind);
  console.log(`kept ${kept.length}, dropped ${dropped.length}, noisy ${meta.noisyRows}, sourceHash ${meta.sourceHash}`);
  console.log(`wrote ${path.join(outDir, "train.jsonl")}`);
  if (dropped.length > 0) {
    console.log(`\n${dropped.length} candidate(s) dropped (did not compile):`);
    for (const d of dropped.slice(0, 15)) console.log(`  [${d.kind}] ${d.fragment}\n    ${d.errors.join("; ")}`);
    if (dropped.length > 15) console.log(`  ... and ${dropped.length - 15} more`);
  }
}

main().catch((error) => {
  console.error(error instanceof Error ? error.message : error);
  process.exit(1);
});
