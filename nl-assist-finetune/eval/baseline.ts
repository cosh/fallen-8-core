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
 * Env:  NL_EVAL_MODEL     model name        (default phi4-mini; set to a fine-tuned
 *                                            model, e.g. f8-delegate, to compare runs)
 *       NL_EVAL_ENDPOINT  Ollama endpoint   (default http://localhost:11434)
 *       NL_EVAL_F8        apiApp base URL   (default http://localhost:5000; must run
 *                                            with EnableDynamicCodeExecution=true)
 *
 * Results are written per-row (resumable) to eval/results/baseline-<model>.json; the
 * summary numbers belong in features/open/nl-assist-finetune/plan.md's run ledger.
 */

import { existsSync, mkdirSync, readFileSync, writeFileSync } from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";
import type { DelegateKind } from "../../fallen-8-web-ui/src/api/types";
import { formatFragment } from "../../fallen-8-web-ui/src/delegate/nl/format";
import { initialMessages, type ChatTurn } from "../../fallen-8-web-ui/src/delegate/nl/generate";
import {
  buildGenerationPrompt,
  extractFragment,
} from "../../fallen-8-web-ui/src/delegate/nl/prompt";

const here = path.dirname(fileURLToPath(import.meta.url));

const MODEL = process.env.NL_EVAL_MODEL ?? "phi4-mini";
const ENDPOINT = process.env.NL_EVAL_ENDPOINT ?? "http://localhost:11434";
const F8 = process.env.NL_EVAL_F8 ?? "http://localhost:5000";
const PER_CALL_TIMEOUT_MS = 6 * 60 * 1000; // CPU inference is slow; be generous.

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
  stats: {
    promptTokens?: number;
    completionTokens?: number;
    durationMs?: number;
    tokensPerSecond?: number;
  } | null;
}

/**
 * Streaming Ollama chat for the harness. The web UI's chatWithModel is non-streaming,
 * which is fine in a browser but trips Node/undici's 5-minute headers timeout on slow
 * CPU generations (headers only arrive when the full body is ready). Streaming delivers
 * headers immediately and chunks every token; the final chunk carries the stats.
 */
async function ollamaChat(
  messages: ChatTurn[],
): Promise<{ content: string; stats: RowResult["stats"] }> {
  const response = await fetch(`${ENDPOINT.replace(/\/+$/, "")}/api/chat`, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({
      model: MODEL,
      messages,
      stream: true,
      options: { temperature: 0.1 },
    }),
    signal: AbortSignal.timeout(PER_CALL_TIMEOUT_MS),
  });
  if (!response.ok || !response.body) {
    throw new Error(`Model endpoint returned HTTP ${response.status}.`);
  }

  let content = "";
  let stats: RowResult["stats"] = null;
  let buffered = "";
  const decoder = new TextDecoder();
  for await (const chunk of response.body) {
    buffered += decoder.decode(chunk as Uint8Array, { stream: true });
    let newline: number;
    while ((newline = buffered.indexOf("\n")) >= 0) {
      const line = buffered.slice(0, newline).trim();
      buffered = buffered.slice(newline + 1);
      if (!line) continue;
      const parsed = JSON.parse(line) as {
        message?: { content?: string };
        done?: boolean;
        total_duration?: number;
        prompt_eval_count?: number;
        eval_count?: number;
        eval_duration?: number;
      };
      content += parsed.message?.content ?? "";
      if (parsed.done) {
        stats = {
          promptTokens: parsed.prompt_eval_count,
          completionTokens: parsed.eval_count,
          durationMs:
            parsed.total_duration !== undefined ? parsed.total_duration / 1e6 : undefined,
          tokensPerSecond:
            parsed.eval_count !== undefined && parsed.eval_duration
              ? parsed.eval_count / (parsed.eval_duration / 1e9)
              : undefined,
        };
      }
    }
  }
  return { content, stats };
}

async function validate(kind: DelegateKind, fragment: string) {
  const response = await fetch(`${F8}/delegates/validate`, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ delegateKind: kind, fragment }),
  });
  if (!response.ok) {
    throw new Error(
      `/delegates/validate returned HTTP ${response.status} - is the apiApp running with Fallen8__Security__EnableDynamicCodeExecution=true?`,
    );
  }
  return (await response.json()) as {
    valid: boolean;
    diagnostics: { severity: string; id: string; message: string }[];
  };
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

  // Preflight both dependencies with a known-good fragment before burning model time.
  const preflight = await validate("VertexFilter", "return (v) => true;");
  if (!preflight.valid) throw new Error("Preflight validate failed unexpectedly.");
  const version = await fetch(`${ENDPOINT}/api/version`);
  if (!version.ok) throw new Error(`Ollama not reachable at ${ENDPOINT}.`);

  const resultsDir = path.join(here, "results");
  mkdirSync(resultsDir, { recursive: true });
  const outFile = path.join(resultsDir, `baseline-${MODEL.replace(/[^\w.-]/g, "_")}.json`);
  const results: RowResult[] = existsSync(outFile)
    ? (JSON.parse(readFileSync(outFile, "utf8")).rows as RowResult[])
    : [];
  const done = new Set(results.map((result) => result.id));

  console.log(`model=${MODEL} endpoint=${ENDPOINT} f8=${F8} rows=${rows.length} (resumed: ${done.size})`);

  for (const row of rows) {
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
      compileErrors: validation.diagnostics
        .filter((d) => d.severity === "error")
        .map((d) => `${d.id} ${d.message}`),
      failedChecks,
      pass: validation.valid && failedChecks.length === 0,
      stats,
    };
    results.push(result);
    writeFileSync(outFile, JSON.stringify({ model: MODEL, rows: results }, null, 2));
    console.log(
      `${result.pass ? "PASS" : "FAIL"} ${row.id} compile=${result.compileValid} checks=${
        failedChecks.length === 0 ? "ok" : failedChecks.join("; ")
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
    return {
      n: subset.length,
      compile: percent(subset.filter((result) => result.compileValid).length, subset.length),
      semanticProxy: percent(subset.filter((result) => result.pass).length, subset.length),
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
