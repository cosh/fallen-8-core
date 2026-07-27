// MIT License
//
// baseline.ts
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
 *       NL_EVAL_F8        apiApp base URL   (default http://localhost:5000; the compile
 *                                            authority — dynamic code is always on)
 *
 * Results are written per-row (resumable) to eval/results/baseline-<model>.json; the
 * summary numbers belong in features/done/nl-assist-finetune/plan.md's run ledger.
 */

import { existsSync, mkdirSync, readFileSync, writeFileSync } from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";
import type {
  AlgorithmContract,
  DelegateKind,
  PluginAuthoringCategory,
} from "../../fallen-8-web-ui/src/api/types";
import { formatFragment } from "../../fallen-8-web-ui/src/delegate/nl/format";
import { initialMessages } from "../../fallen-8-web-ui/src/delegate/nl/generate";
import {
  buildGenerationPrompt,
  extractFragment,
} from "../../fallen-8-web-ui/src/delegate/nl/prompt";
import { buildPluginGenerationPrompt, extractType } from "../../fallen-8-web-ui/src/plugin/nl/pluginPrompt";
import { scaffoldFor } from "../../fallen-8-web-ui/src/plugin/scaffolds";
import {
  compileErrors,
  ENDPOINT,
  F8,
  type GenStats,
  MODEL,
  ollamaChat,
  ollamaReachable,
  validate,
  validatePlugin,
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

/**
 * WHOLE-TYPE plugin eval (feature plugin-registration): a parallel, COMPILE-ONLY path.
 * A plugin is a whole C# type, so there is no lambda to run through /subgraph — the
 * element-set semantic gate (fixture.ts) does not apply. We generate one first-pass draft
 * per row and score it purely on whether it compiles + satisfies the contract via
 * POST /plugins/{category}/validate.
 */
interface PluginEvalRow {
  id: string;
  category: PluginAuthoringCategory;
  contract?: AlgorithmContract;
  name: string;
  intent: string;
  reference: string;
}

interface PluginRowResult {
  id: string;
  category: PluginAuthoringCategory;
  contract?: AlgorithmContract;
  name: string;
  source: string;
  compileValid: boolean;
  compileError: string | null;
  pass: boolean;
  stats: GenStats | null;
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

  // Optional sibling set of WHOLE-TYPE plugin rows (feature plugin-registration). Absent on an
  // older checkout: the plugin path simply does nothing then.
  const pluginSetPath = path.join(here, "plugin-eval-set.json");
  const pluginEvalRows: PluginEvalRow[] = existsSync(pluginSetPath)
    ? (JSON.parse(readFileSync(pluginSetPath, "utf8")).rows as PluginEvalRow[])
    : [];

  const rescore = process.argv.includes("--rescore");
  const semantic = process.argv.includes("--semantic");

  if (!rescore) {
    // Preflight both dependencies with a known-good fragment before burning model time.
    const preflight = await validate("VertexFilter", "return (v) => true;");
    if (!preflight.valid) throw new Error("Preflight validate failed unexpectedly.");
    if (!(await ollamaReachable())) throw new Error(`Ollama not reachable at ${ENDPOINT}.`);
    // The plugin authority is gated; validatePlugin surfaces a clear 403 if the capability is off.
    if (pluginEvalRows.length > 0) {
      const pluginPreflight = await validatePlugin("function", {
        name: "PreflightFunction",
        sourceCode: scaffoldFor("function", "Path", "PreflightFunction"),
      });
      if (!pluginPreflight.valid) throw new Error("Plugin preflight validate failed unexpectedly.");
    }
  }

  // FT-8 semantic scoring seeds the fixture graph on the apiApp (idempotent per instance).
  if (semantic) {
    const info = await ensureFixture();
    console.log(`semantic fixture: ${info.seeded ? "seeded" : "present"} (${info.vertices}v/${info.edges}e)`);
  }

  const resultsDir = path.join(here, "results");
  mkdirSync(resultsDir, { recursive: true });
  const outFile = path.join(resultsDir, `baseline-${MODEL.replace(/[^\w.-]/g, "_")}.json`);
  const persisted = existsSync(outFile)
    ? (JSON.parse(readFileSync(outFile, "utf8")) as { rows?: RowResult[]; pluginRows?: PluginRowResult[] })
    : {};
  const results: RowResult[] = persisted.rows ?? [];
  const pluginResults: PluginRowResult[] = persisted.pluginRows ?? [];
  const done = new Set(results.map((result) => result.id));
  const donePlugins = new Set(pluginResults.map((result) => result.id));
  // Both result arrays are written together on every step so a resumed run keeps each.
  const save = () =>
    writeFileSync(outFile, JSON.stringify({ model: MODEL, rows: results, pluginRows: pluginResults }, null, 2));

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
    // Plugin rescore is compile-only: re-validate each recorded source (no model calls).
    for (const result of pluginResults) {
      const validation = await validatePlugin(result.category, {
        name: result.name,
        contract: result.category === "algorithm" ? (result.contract ?? "Path") : undefined,
        sourceCode: result.source,
      });
      result.compileValid = validation.valid;
      result.compileError = validation.error;
      result.pass = validation.valid;
    }
  }

  console.log(
    `model=${MODEL} endpoint=${ENDPOINT} f8=${F8} rows=${rows.length} (resumed: ${done.size})` +
      (pluginEvalRows.length > 0 ? ` pluginRows=${pluginEvalRows.length} (resumed: ${donePlugins.size})` : ""),
  );

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
    save();
    const sem = semantic
      ? ` sem=${result.semanticApplicable ? (result.semanticPass ? "ok" : "MISS") : "n/a"}`
      : "";
    console.log(
      `${result.pass ? "PASS" : "FAIL"} ${row.id} compile=${result.compileValid} checks=${
        failedChecks.length === 0 ? "ok" : failedChecks.join("; ")
      }${sem} ${result.stats ? `${((result.stats.durationMs ?? 0) / 1000).toFixed(1)}s ${result.stats.tokensPerSecond?.toFixed(1) ?? "?"} tok/s` : ""}`,
    );
  }

  // Whole-type plugin rows (compile-only). One first-pass draft per row through the plugin
  // authority; no semantic gate (a whole type is not a /subgraph filter lambda).
  for (const row of pluginEvalRows) {
    if (rescore) break;
    if (donePlugins.has(row.id)) continue;
    const contract = row.contract ?? "Path"; // ignored by the prompt for a function
    const scaffold = scaffoldFor(row.category, contract, row.name);
    const prompt = buildPluginGenerationPrompt({
      category: row.category,
      contract,
      name: row.name,
      scaffold,
      intent: row.intent,
    });
    const { content, stats } = await ollamaChat(initialMessages(prompt));
    const source = extractType(content);
    const validation = await validatePlugin(row.category, {
      name: row.name,
      contract: row.category === "algorithm" ? contract : undefined,
      sourceCode: source,
    });
    const result: PluginRowResult = {
      id: row.id,
      category: row.category,
      contract: row.contract,
      name: row.name,
      source,
      compileValid: validation.valid,
      compileError: validation.error,
      pass: validation.valid,
      stats,
    };
    pluginResults.push(result);
    save();
    console.log(
      `${result.pass ? "PASS" : "FAIL"} ${row.id} compile=${result.compileValid}${
        result.compileError ? ` (${result.compileError.split("\n")[0]})` : ""
      } ${result.stats ? `${((result.stats.durationMs ?? 0) / 1000).toFixed(1)}s ${result.stats.tokensPerSecond?.toFixed(1) ?? "?"} tok/s` : ""}`,
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

  // Plugin summary (compile-only). Bucketed by the coverage buckets: the algorithm contracts
  // plus the function category.
  const pluginBucketOf = (result: PluginRowResult) =>
    result.category === "function" ? "function" : result.contract ?? "algorithm";
  const summarizePlugins = (subset: PluginRowResult[]) => {
    const withStats = subset.filter((result) => result.stats?.durationMs !== undefined);
    const meanSeconds =
      withStats.reduce((sum, result) => sum + (result.stats!.durationMs ?? 0), 0) /
      Math.max(1, withStats.length) /
      1000;
    return {
      n: subset.length,
      compile: percent(subset.filter((result) => result.compileValid).length, subset.length),
      meanSecondsPerDraft: Number(meanSeconds.toFixed(1)),
    };
  };
  const pluginBuckets = [...new Set(pluginResults.map(pluginBucketOf))];
  const pluginSummary =
    pluginResults.length > 0
      ? {
          overall: summarizePlugins(pluginResults),
          perContract: Object.fromEntries(
            pluginBuckets.map((b) => [b, summarizePlugins(pluginResults.filter((r) => pluginBucketOf(r) === b))]),
          ),
        }
      : undefined;

  writeFileSync(
    outFile,
    JSON.stringify({ ...summary, pluginSummary, rows: results, pluginRows: pluginResults }, null, 2),
  );
  console.log("\n=== summary (fragments) ===");
  console.table({ overall: summary.overall, ...summary.perKind });
  if (pluginSummary) {
    console.log("\n=== summary (plugins, compile-only) ===");
    console.table({ overall: pluginSummary.overall, ...pluginSummary.perContract });
  }
  console.log(`results: ${outFile}`);
}

main().catch((error) => {
  console.error(error instanceof Error ? error.message : error);
  process.exit(1);
});
