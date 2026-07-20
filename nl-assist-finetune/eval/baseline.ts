/**
 * First-pass baseline harness (nl-assist-finetune plan phase 1, spec Stage 7).
 *
 * For every eval-set row: assemble the SHIPPING prompt (imported from the web UI, so the
 * measurement can't drift from the product), make ONE model call (no refine loop - the
 * metric is first-pass quality), format, then score:
 *   - compile:   POST /delegates/validate (the product's own compile authority)
 *   - checks:    the row's mustMatch/mustNotMatch regexes (semantic proxy until FT-8)
 *   - perf:      the provider's token/duration stats per draft
 *
 * Run:  npx tsx nl-assist-finetune/eval/baseline.ts
 *       --rescore   recompute checks + summary from recorded fragments (no model
 *                   calls) - for when an eval-set check turns out too strict/lax
 * Env:  NL_EVAL_MODEL     model name        (default phi4-mini; set to a fine-tuned
 *                                            model, e.g. phi4-f8-mini, to compare runs)
 *       NL_EVAL_ENDPOINT  Ollama endpoint   (default http://localhost:11434)
 *       NL_EVAL_F8        apiApp base URL   (default http://localhost:5000; must run
 *                                            with EnableDynamicCodeExecution=true)
 *
 * Results are written per-row (resumable) to eval/results/baseline-<model>.json; the
 * summary numbers belong in features/done/nl-assist-finetune/plan.md's run ledger.
 */

import { existsSync, mkdirSync, readFileSync, writeFileSync } from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";
import type { DelegateKind } from "../../fallen-8-web-ui/src/api/types";
import { formatFragment } from "../../fallen-8-web-ui/src/delegate/nl/format";
import { initialMessages } from "../../fallen-8-web-ui/src/delegate/nl/generate";
import {
  buildGenerationPrompt,
  extractFragment,
} from "../../fallen-8-web-ui/src/delegate/nl/prompt";
import {
  compileErrors,
  ENDPOINT,
  F8,
  type GenStats,
  MODEL,
  ollamaChat,
  ollamaReachable,
  validate,
} from "../shared/f8";
import { compareSemantics, ensureFixture } from "./fixture";

const here = path.dirname(fileURLToPath(import.meta.url));

interface EvalRow {
  id: string;
  kind: DelegateKind;
  intent: string;
  reference: string;
  mustMatch: string[];
  mustNotMatch: string[];
}

interface RowResult {
  id: string;
  kind: DelegateKind;
  intent: string;
  fragment: string;
  compileValid: boolean;
  compileErrors: string[];
  failedChecks: string[];
  pass: boolean;
  stats: GenStats | null;
  /** FT-8 element-set verdict (only when run with --semantic). undefined pass = not applicable. */
  semanticApplicable?: boolean;
  semanticPass?: boolean;
}

function runChecks(row: EvalRow, fragment: string): string[] {
  const failed: string[] = [];
  for (const pattern of row.mustMatch) {
    if (!new RegExp(pattern).test(fragment)) failed.push(`missing: ${pattern}`);
  }
  for (const pattern of row.mustNotMatch) {
    if (new RegExp(pattern).test(fragment)) failed.push(`forbidden: ${pattern}`);
  }
  return failed;
}

function percent(part: number, total: number): string {
  return total === 0 ? "-" : `${((100 * part) / total).toFixed(0)}%`;
}

async function main() {
  const rows = (
    JSON.parse(readFileSync(path.join(here, "eval-set.json"), "utf8")) as {
      rows: EvalRow[];
    }
  ).rows;

  const rescore = process.argv.includes("--rescore");
  const semantic = process.argv.includes("--semantic");

  if (!rescore) {
    // Preflight both dependencies with a known-good fragment before burning model time.
    const preflight = await validate("VertexFilter", "return (v) => true;");
    if (!preflight.valid) throw new Error("Preflight validate failed unexpectedly.");
    if (!(await ollamaReachable())) throw new Error(`Ollama not reachable at ${ENDPOINT}.`);
  }

  // FT-8 semantic scoring seeds the fixture graph on the apiApp (idempotent per instance).
  if (semantic) {
    const info = await ensureFixture();
    console.log(`semantic fixture: ${info.seeded ? "seeded" : "present"} (${info.vertices}v/${info.edges}e)`);
  }

  const resultsDir = path.join(here, "results");
  mkdirSync(resultsDir, { recursive: true });
  const outFile = path.join(resultsDir, `baseline-${MODEL.replace(/[^\w.-]/g, "_")}.json`);
  const results: RowResult[] = existsSync(outFile)
    ? (JSON.parse(readFileSync(outFile, "utf8")).rows as RowResult[])
    : [];
  const done = new Set(results.map((result) => result.id));

  if (rescore) {
    for (const result of results) {
      const row = rows.find((r) => r.id === result.id);
      if (!row) continue;
      result.failedChecks = runChecks(row, result.fragment);
      result.pass = result.compileValid && result.failedChecks.length === 0;
      if (semantic) {
        const verdict = await compareSemantics(row.kind, row.reference, result.fragment);
        result.semanticApplicable = verdict.applicable;
        result.semanticPass = verdict.applicable ? verdict.pass : undefined;
      }
    }
  }

  console.log(`model=${MODEL} endpoint=${ENDPOINT} f8=${F8} rows=${rows.length} (resumed: ${done.size})`);

  for (const row of rows) {
    if (rescore) break;
    if (done.has(row.id)) continue;
    const prompt = buildGenerationPrompt(row.kind, row.intent);
    const { content, stats } = await ollamaChat(initialMessages(prompt));
    const fragment = formatFragment(extractFragment(content));
    const validation = await validate(row.kind, fragment);
    const failedChecks = runChecks(row, fragment);
    const result: RowResult = {
      id: row.id,
      kind: row.kind,
      intent: row.intent,
      fragment,
      compileValid: validation.valid,
      compileErrors: compileErrors(validation),
      failedChecks,
      pass: validation.valid && failedChecks.length === 0,
      stats,
    };
    if (semantic) {
      const verdict = await compareSemantics(row.kind, row.reference, fragment);
      result.semanticApplicable = verdict.applicable;
      result.semanticPass = verdict.applicable ? verdict.pass : undefined;
    }
    results.push(result);
    writeFileSync(outFile, JSON.stringify({ model: MODEL, rows: results }, null, 2));
    const sem = semantic
      ? ` sem=${result.semanticApplicable ? (result.semanticPass ? "ok" : "MISS") : "n/a"}`
      : "";
    console.log(
      `${result.pass ? "PASS" : "FAIL"} ${row.id} compile=${result.compileValid} checks=${
        failedChecks.length === 0 ? "ok" : failedChecks.join("; ")
      }${sem} ${result.stats ? `${((result.stats.durationMs ?? 0) / 1000).toFixed(1)}s ${result.stats.tokensPerSecond?.toFixed(1) ?? "?"} tok/s` : ""}`,
    );
  }

  // Summary - overall and per kind.
  const kinds = [...new Set(results.map((result) => result.kind))];
  const summarize = (subset: RowResult[]) => {
    const withStats = subset.filter((result) => result.stats?.durationMs !== undefined);
    const meanSeconds =
      withStats.reduce((sum, result) => sum + (result.stats!.durationMs ?? 0), 0) /
      Math.max(1, withStats.length) /
      1000;
    const meanTokensPerSecond =
      withStats.reduce((sum, result) => sum + (result.stats!.tokensPerSecond ?? 0), 0) /
      Math.max(1, withStats.length);
    const applicable = subset.filter((result) => result.semanticApplicable);
    return {
      n: subset.length,
      compile: percent(subset.filter((result) => result.compileValid).length, subset.length),
      semanticProxy: percent(subset.filter((result) => result.pass).length, subset.length),
      // FT-8 element-set rate over the rows it applies to (n/a rows excluded); the "N"
      // column is that applicable count, so a small denominator is never hidden.
      ...(semantic
        ? {
            semantic: percent(applicable.filter((result) => result.semanticPass).length, applicable.length),
            semanticN: applicable.length,
          }
        : {}),
      meanSecondsPerDraft: Number(meanSeconds.toFixed(1)),
      meanTokensPerSecond: Number(meanTokensPerSecond.toFixed(1)),
    };
  };

  const summary = {
    model: MODEL,
    overall: summarize(results),
    perKind: Object.fromEntries(
      kinds.map((kind) => [kind, summarize(results.filter((result) => result.kind === kind))]),
    ),
  };
  writeFileSync(outFile, JSON.stringify({ ...summary, rows: results }, null, 2));
  console.log("\n=== summary ===");
  console.table({ overall: summary.overall, ...summary.perKind });
  console.log(`results: ${outFile}`);
}

main().catch((error) => {
  console.error(error instanceof Error ? error.message : error);
  process.exit(1);
});
