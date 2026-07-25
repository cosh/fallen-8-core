/**
 * FL-3 consolidation (feature nl-assist-feedback-loop). Turns the opt-in captures exported by
 * the NL-assist panels into training rows, safely:
 *
 *   ingest capture JSONL  ->  keep 👍 only  ->  re-validate each candidate via the product's
 *   compile authority (a non-compiling capture never enters training)  ->  drop rows whose
 *   intent is in the held-out eval set (train/test isolation)  ->  dedupe against the generated
 *   + already-captured corpus  ->  append survivors to dataset/captured.jsonl in the trainer's
 *   row format.
 *
 * TWO capture shapes are ingested (feature plugin-registration):
 *   - the delegate FRAGMENT panel exports `{ delegateKind, intent, fragment, verdict, ts }`,
 *     re-validated via POST /delegates/validate and written as a fragment corpus row;
 *   - the plugin WHOLE-TYPE panel exports `{ kind:"plugin", category, contract?, name, intent,
 *     source, verdict, ts }` (contract present only for an algorithm), re-validated via POST
 *     /plugins/{category}/validate and written as a plugin corpus row (buildPluginGenerationPrompt
 *     + the source as the assistant turn). A malformed/foreign line is skipped, never aborts.
 *
 * It NEVER writes eval/eval-set.json or eval/plugin-eval-set.json — the held-out sets only grow
 * by hand, so ledger rows stay comparable (spec FT-4). 👎 rows are dropped: a bad draft with no
 * correction is not a usable positive; to contribute a fix, 👍 the corrected draft instead.
 *
 * Run:  npx tsx nl-assist-finetune/feedback/consolidate.ts [capture1.jsonl capture2.jsonl ...]
 *       (no paths -> reads nl-assist-finetune/feedback/inbox/*.jsonl)
 * Needs the apiApp compile authority (set NL_EVAL_F8 if not http://localhost:5000). The plugin
 * validate route is gated by the dynamic-plugin capability (validatePlugin surfaces a clear 403
 * error). Then retrain: ./run.sh train reads dataset/captured.jsonl alongside train.jsonl.
 */

import { appendFileSync, existsSync, mkdirSync, readdirSync, readFileSync } from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";
import type {
  AlgorithmContract,
  DelegateKind,
  PluginAuthoringCategory,
} from "../../fallen-8-web-ui/src/api/types";
import { initialMessages, type ChatTurn } from "../../fallen-8-web-ui/src/delegate/nl/generate";
import { buildGenerationPrompt } from "../../fallen-8-web-ui/src/delegate/nl/prompt";
import { buildPluginGenerationPrompt } from "../../fallen-8-web-ui/src/plugin/nl/pluginPrompt";
import { scaffoldFor } from "../../fallen-8-web-ui/src/plugin/scaffolds";
import { validate, validatePlugin } from "../shared/f8";

const here = path.dirname(fileURLToPath(import.meta.url));
const datasetDir = path.resolve(here, "../dataset");
const inboxDir = path.join(here, "inbox");
const corpusPath = path.join(datasetDir, "train.jsonl");
const capturedPath = path.join(datasetDir, "captured.jsonl");
const evalSetPath = path.resolve(here, "../eval/eval-set.json");
const pluginEvalSetPath = path.resolve(here, "../eval/plugin-eval-set.json");

/** A verdict-carrying capture, either a delegate fragment or a whole-type plugin. */
type Capture =
  | {
      shape: "delegate";
      delegateKind: DelegateKind;
      intent: string;
      fragment: string;
      verdict: "up" | "down" | null;
    }
  | {
      shape: "plugin";
      category: PluginAuthoringCategory;
      contract?: AlgorithmContract;
      name: string;
      intent: string;
      source: string;
      verdict: "up" | "down" | null;
    };

/** A fragment corpus row (unchanged trainer shape). */
interface DelegateCorpusRow {
  delegateKind: DelegateKind;
  intent: string;
  fragment: string;
  source: string;
  noisy: boolean;
  messages: ChatTurn[];
}

/** A whole-type corpus row — the same shape the generator writes for plugin rows. */
interface PluginCorpusRow {
  kind: "plugin";
  category: PluginAuthoringCategory;
  contract?: AlgorithmContract;
  name: string;
  intent: string;
  source: string;
  messages: ChatTurn[];
}

type CorpusRow = DelegateCorpusRow | PluginCorpusRow;

const normIntent = (s: string) => s.trim().toLowerCase().replace(/\s+/g, " ");
const verdictOf = (v: unknown): "up" | "down" | null => (v === "up" || v === "down" ? v : null);

/** Row keys are shape-tagged so a fragment and a plugin can never collide. */
const delegateKey = (kind: string, intent: string, fragment: string) =>
  `d|${kind}|${normIntent(intent)}|${fragment.trim()}`;
const pluginKey = (category: string, contract: string | undefined, intent: string, source: string) =>
  `p|${category}|${contract ?? ""}|${normIntent(intent)}|${source.trim()}`;

const captureKey = (c: Capture): string =>
  c.shape === "plugin"
    ? pluginKey(c.category, c.contract, c.intent, c.source)
    : delegateKey(c.delegateKind, c.intent, c.fragment);

/** Dedup key for an already-persisted corpus row (either shape); null if unrecognized. */
function corpusKeyOf(row: Record<string, unknown>): string | null {
  if (row.kind === "plugin") {
    if (typeof row.category !== "string" || typeof row.intent !== "string" || typeof row.source !== "string") {
      return null;
    }
    return pluginKey(
      row.category,
      typeof row.contract === "string" ? row.contract : undefined,
      row.intent,
      row.source,
    );
  }
  if (typeof row.delegateKind === "string" && typeof row.intent === "string" && typeof row.fragment === "string") {
    return delegateKey(row.delegateKind, row.intent, row.fragment);
  }
  return null;
}

/** Classify one raw capture line into a typed Capture, or null if it is malformed/foreign. */
function classify(row: Record<string, unknown>): Capture | null {
  const verdict = verdictOf(row.verdict);
  if (row.kind === "plugin") {
    if (
      typeof row.category !== "string" ||
      typeof row.name !== "string" ||
      typeof row.intent !== "string" ||
      typeof row.source !== "string"
    ) {
      return null;
    }
    return {
      shape: "plugin",
      category: row.category as PluginAuthoringCategory,
      contract: typeof row.contract === "string" ? (row.contract as AlgorithmContract) : undefined,
      name: row.name,
      intent: row.intent,
      source: row.source,
      verdict,
    };
  }
  if (
    typeof row.delegateKind === "string" &&
    typeof row.intent === "string" &&
    typeof row.fragment === "string"
  ) {
    return {
      shape: "delegate",
      delegateKind: row.delegateKind as DelegateKind,
      intent: row.intent,
      fragment: row.fragment,
      verdict,
    };
  }
  return null;
}

/** Tolerant JSONL reader for machine-written corpus files (dedup + counts). */
function readJsonl(file: string): Record<string, unknown>[] {
  return readFileSync(file, "utf8")
    .split("\n")
    .map((line) => line.trim())
    .filter(Boolean)
    .map((line) => JSON.parse(line) as Record<string, unknown>);
}

/**
 * Read capture files, skipping (and counting) any line that is not valid JSON or not a
 * recognized capture shape — a stray/foreign line must never abort the whole run.
 */
function readCaptures(files: string[]): { captures: Capture[]; malformed: number } {
  const captures: Capture[] = [];
  let malformed = 0;
  for (const file of files) {
    for (const line of readFileSync(file, "utf8").split("\n")) {
      const trimmed = line.trim();
      if (!trimmed) continue;
      let parsed: Record<string, unknown>;
      try {
        parsed = JSON.parse(trimmed) as Record<string, unknown>;
      } catch {
        malformed++;
        continue;
      }
      const capture = classify(parsed);
      if (capture) captures.push(capture);
      else malformed++;
    }
  }
  return { captures, malformed };
}

/** The trainer row for a surviving capture (fragment or whole-type plugin). */
function toCorpusRow(capture: Capture): CorpusRow {
  if (capture.shape === "plugin") {
    const { category, name, intent, source } = capture;
    // A function ignores the contract, but scaffoldFor/buildPluginGenerationPrompt still need one.
    const contract = capture.contract ?? "Path";
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
  return {
    delegateKind: capture.delegateKind,
    intent: capture.intent,
    fragment: capture.fragment,
    source: "capture",
    noisy: false,
    messages: [
      ...initialMessages(buildGenerationPrompt(capture.delegateKind, capture.intent)),
      { role: "assistant", content: capture.fragment },
    ],
  };
}

/** Re-validate a capture through the matching compile authority. */
async function isValid(capture: Capture): Promise<boolean> {
  if (capture.shape === "plugin") {
    const contract = capture.contract ?? "Path";
    const result = await validatePlugin(capture.category, {
      name: capture.name,
      contract: capture.category === "algorithm" ? contract : undefined,
      sourceCode: capture.source,
    });
    return result.valid;
  }
  const result = await validate(capture.delegateKind, capture.fragment);
  return result.valid;
}

/** A short breakdown bucket label for the per-run stats table. */
const bucketLabel = (capture: Capture): string =>
  capture.shape === "plugin"
    ? `plugin:${capture.category === "function" ? "function" : capture.contract ?? "algorithm"}`
    : capture.delegateKind;

async function main() {
  const paths = process.argv.slice(2).filter((arg) => !arg.startsWith("-"));
  const files =
    paths.length > 0
      ? paths
      : existsSync(inboxDir)
        ? readdirSync(inboxDir).filter((f) => f.endsWith(".jsonl")).map((f) => path.join(inboxDir, f))
        : [];
  if (files.length === 0) {
    console.error(`No capture files. Pass paths, or drop exported *.jsonl into ${inboxDir}.`);
    process.exit(1);
  }

  // Preflight the compile authority before validating captures.
  const preflight = await validate("VertexFilter", "return (v) => true;");
  if (!preflight.valid) throw new Error("Preflight validate failed - is the apiApp healthy?");

  const { captures, malformed } = readCaptures(files);
  const positives = captures.filter((c) => c.verdict === "up");

  // Dedupe target: everything already in the corpus (generated + previously captured).
  const corpusKeys = new Set<string>();
  for (const file of [corpusPath, capturedPath]) {
    if (existsSync(file)) {
      for (const row of readJsonl(file)) {
        const key = corpusKeyOf(row);
        if (key) corpusKeys.add(key);
      }
    }
  }
  // Train/test isolation: never train on an intent that is in a held-out eval set (both the
  // fragment eval set and the plugin eval set).
  const evalIntents = new Set<string>();
  for (const file of [evalSetPath, pluginEvalSetPath]) {
    if (existsSync(file)) {
      for (const row of JSON.parse(readFileSync(file, "utf8")).rows as { intent: string }[]) {
        evalIntents.add(normIntent(row.intent));
      }
    }
  }

  const survivors: CorpusRow[] = [];
  const survivorCaptures: Capture[] = [];
  const seen = new Set<string>();
  const dropped: Record<string, number> = {
    "👎 (no correction)": captures.length - positives.length,
    "malformed/foreign line": malformed,
  };
  const bump = (reason: string) => (dropped[reason] = (dropped[reason] ?? 0) + 1);

  for (const capture of positives) {
    if (evalIntents.has(normIntent(capture.intent))) {
      bump("held-out eval intent");
      continue;
    }
    const key = captureKey(capture);
    if (corpusKeys.has(key) || seen.has(key)) {
      bump("duplicate (already in corpus or this batch)");
      continue;
    }
    if (!(await isValid(capture))) {
      bump("does not compile");
      continue;
    }
    seen.add(key);
    survivors.push(toCorpusRow(capture));
    survivorCaptures.push(capture);
  }

  mkdirSync(datasetDir, { recursive: true });
  if (survivors.length > 0) {
    appendFileSync(capturedPath, survivors.map((row) => JSON.stringify(row)).join("\n") + "\n");
  }

  const perBucket = survivorCaptures.reduce<Record<string, number>>((acc, capture) => {
    const label = bucketLabel(capture);
    acc[label] = (acc[label] ?? 0) + 1;
    return acc;
  }, {});
  const capturedTotal = existsSync(capturedPath) ? readJsonl(capturedPath).length : 0;

  console.log("\n=== consolidation ===");
  console.log(`captures read: ${captures.length}; added this run: ${survivors.length}`);
  console.log(`dropped: ${Object.entries(dropped).filter(([, n]) => n > 0).map(([r, n]) => `${n} ${r}`).join(", ") || "none"}`);
  if (survivors.length > 0) console.table(perBucket);
  console.log(`dataset/captured.jsonl now holds ${capturedTotal} captured example(s).`);
  console.log(
    survivors.length > 0
      ? "Retrain to fold them in: ./run.sh train (it reads captured.jsonl alongside train.jsonl). " +
          "Retrain when it's worth it - e.g. >=50 new pairs, a new eval failure mode, or a contract change."
      : "Nothing new to add.",
  );
}

main().catch((error) => {
  console.error(error instanceof Error ? error.message : error);
  process.exit(1);
});
