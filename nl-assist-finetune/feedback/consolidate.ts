/**
 * FL-3 consolidation (feature nl-assist-feedback-loop). Turns the opt-in captures exported by
 * the NL-assist panel (FL-2) into training rows, safely:
 *
 *   ingest capture JSONL  ->  keep 👍 only  ->  re-validate each fragment via
 *   POST /delegates/validate (a non-compiling capture never enters training)  ->  drop rows
 *   whose intent is in the held-out eval set (train/test isolation)  ->  dedupe against the
 *   generated + already-captured corpus  ->  append survivors to dataset/captured.jsonl in the
 *   trainer's row format.
 *
 * It NEVER writes eval/eval-set.json — the held-out set only grows by hand, so ledger rows
 * stay comparable (spec FT-4). 👎 rows are dropped: a bad draft with no correction is not a
 * usable positive; to contribute a fix, 👍 the corrected draft instead.
 *
 * Run:  npx tsx nl-assist-finetune/feedback/consolidate.ts [capture1.jsonl capture2.jsonl ...]
 *       (no paths -> reads nl-assist-finetune/feedback/inbox/*.jsonl)
 * Needs the apiApp compile authority (set NL_EVAL_F8 if not http://localhost:5000). Then
 * retrain: ./run.sh train reads dataset/captured.jsonl alongside the generated train.jsonl.
 */

import { appendFileSync, existsSync, mkdirSync, readdirSync, readFileSync } from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";
import type { DelegateKind } from "../../fallen-8-web-ui/src/api/types";
import { initialMessages, type ChatTurn } from "../../fallen-8-web-ui/src/delegate/nl/generate";
import { buildGenerationPrompt } from "../../fallen-8-web-ui/src/delegate/nl/prompt";
import { validate } from "../shared/f8";

const here = path.dirname(fileURLToPath(import.meta.url));
const datasetDir = path.resolve(here, "../dataset");
const inboxDir = path.join(here, "inbox");
const corpusPath = path.join(datasetDir, "train.jsonl");
const capturedPath = path.join(datasetDir, "captured.jsonl");
const evalSetPath = path.resolve(here, "../eval/eval-set.json");

interface Capture {
  delegateKind: DelegateKind;
  intent: string;
  fragment: string;
  verdict: "up" | "down" | null;
  ts?: number;
}

interface CorpusRow {
  delegateKind: DelegateKind;
  intent: string;
  fragment: string;
  source: string;
  noisy: boolean;
  messages: ChatTurn[];
}

const normIntent = (s: string) => s.trim().toLowerCase().replace(/\s+/g, " ");
const rowKey = (kind: string, intent: string, fragment: string) =>
  `${kind}|${normIntent(intent)}|${fragment.trim()}`;

function readJsonl(file: string): Record<string, unknown>[] {
  return readFileSync(file, "utf8")
    .split("\n")
    .map((line) => line.trim())
    .filter(Boolean)
    .map((line) => JSON.parse(line) as Record<string, unknown>);
}

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

  const captures = files.flatMap((file) => readJsonl(file)) as unknown as Capture[];
  const positives = captures.filter((c) => c.verdict === "up");

  // Dedupe target: everything already in the corpus (generated + previously captured).
  const corpusKeys = new Set<string>();
  for (const file of [corpusPath, capturedPath]) {
    if (existsSync(file)) {
      for (const row of readJsonl(file) as unknown as CorpusRow[]) {
        corpusKeys.add(rowKey(row.delegateKind, row.intent, row.fragment));
      }
    }
  }
  // Train/test isolation: never train on an intent that is in the held-out eval set.
  const evalIntents = new Set<string>();
  if (existsSync(evalSetPath)) {
    for (const row of JSON.parse(readFileSync(evalSetPath, "utf8")).rows as { intent: string }[]) {
      evalIntents.add(normIntent(row.intent));
    }
  }

  const survivors: CorpusRow[] = [];
  const seen = new Set<string>();
  const dropped: Record<string, number> = { "👎 (no correction)": captures.length - positives.length };
  const bump = (reason: string) => (dropped[reason] = (dropped[reason] ?? 0) + 1);

  for (const capture of positives) {
    if (evalIntents.has(normIntent(capture.intent))) {
      bump("held-out eval intent");
      continue;
    }
    const key = rowKey(capture.delegateKind, capture.intent, capture.fragment);
    if (corpusKeys.has(key) || seen.has(key)) {
      bump("duplicate (already in corpus or this batch)");
      continue;
    }
    const result = await validate(capture.delegateKind, capture.fragment);
    if (!result.valid) {
      bump("does not compile");
      continue;
    }
    seen.add(key);
    survivors.push({
      delegateKind: capture.delegateKind,
      intent: capture.intent,
      fragment: capture.fragment,
      source: "capture",
      noisy: false,
      messages: [
        ...initialMessages(buildGenerationPrompt(capture.delegateKind, capture.intent)),
        { role: "assistant", content: capture.fragment },
      ],
    });
  }

  mkdirSync(datasetDir, { recursive: true });
  if (survivors.length > 0) {
    appendFileSync(capturedPath, survivors.map((row) => JSON.stringify(row)).join("\n") + "\n");
  }

  const perKind = survivors.reduce<Record<string, number>>((acc, row) => {
    acc[row.delegateKind] = (acc[row.delegateKind] ?? 0) + 1;
    return acc;
  }, {});
  const capturedTotal = existsSync(capturedPath) ? readJsonl(capturedPath).length : 0;

  console.log("\n=== consolidation ===");
  console.log(`captures read: ${captures.length}; added this run: ${survivors.length}`);
  console.log(`dropped: ${Object.entries(dropped).filter(([, n]) => n > 0).map(([r, n]) => `${n} ${r}`).join(", ") || "none"}`);
  if (survivors.length > 0) console.table(perKind);
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
