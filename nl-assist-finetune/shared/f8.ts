// MIT License
//
// f8.ts
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
 * Shared harness plumbing (nl-assist-finetune). One definition of the external
 * dependencies every script in this pipeline talks to:
 *   - the F8 apiApp's POST /delegates/validate - the product's compile authority for the
 *     fragment (delegate lambda-body) surface, used both as the training-set filter (spec
 *     FT-2) and the eval metric (FT-4);
 *   - the F8 apiApp's POST /plugins/{category}/validate - the compile authority for the
 *     WHOLE-TYPE plugin surface (feature plugin-registration). Same role, different shape:
 *     a plugin is a complete C# type, not a lambda body. Unlike /delegates/validate this
 *     endpoint is gated by the dynamic-plugin capability + auth (403 when disabled);
 *   - the Ollama chat endpoint - the model transport, streamed so slow generations
 *     don't trip undici's headers timeout.
 * Baseline eval, semantic eval, and the dataset generator all import from here so the
 * authority and the transport can't drift between them (repo rule: one home per thing).
 *
 * Env (shared by every script):
 *   NL_EVAL_MODEL     model name       (default phi4-mini)
 *   NL_EVAL_ENDPOINT  Ollama endpoint  (default http://localhost:11434)
 *   NL_EVAL_F8        apiApp base URL  (default http://localhost:5000; the compile
 *                                       authority — dynamic code is always on)
 */

import type {
  DelegateKind,
  PluginAuthoringCategory,
  PluginValidationResult,
  PluginValidationSpecification,
} from "../../fallen-8-web-ui/src/api/types";
import type { ChatTurn } from "../../fallen-8-web-ui/src/delegate/nl/generate";

export const MODEL = process.env.NL_EVAL_MODEL ?? "phi4-mini";
export const ENDPOINT = (process.env.NL_EVAL_ENDPOINT ?? "http://localhost:11434").replace(
  /\/+$/,
  "",
);
export const F8 = (process.env.NL_EVAL_F8 ?? "http://localhost:5000").replace(/\/+$/, "");

/** Generous per-call ceiling: CPU inference is slow; a GPU box finishes far sooner. */
export const PER_CALL_TIMEOUT_MS = 6 * 60 * 1000;

export interface GenStats {
  promptTokens?: number;
  completionTokens?: number;
  durationMs?: number;
  tokensPerSecond?: number;
}

export interface ValidationResult {
  valid: boolean;
  diagnostics: { severity: string; id: string; message: string; line?: number; column?: number }[];
}

const sleep = (ms: number) => new Promise((resolve) => setTimeout(resolve, ms));

/**
 * fetch against the apiApp that transparently retries the sensitive-endpoint 429.
 *
 * /delegates/validate and /subgraph sit behind a fixed-window limiter (default 30 requests
 * / 10 s). Batch callers (the dataset generator, the semantic-eval harness) burst past it,
 * so we retry - honouring Retry-After when sent, otherwise backing off ~1.5 s until the
 * window replenishes - keeping callers decoupled from the server's rate rather than
 * hard-coding a client-side one. `path` is relative to the apiApp base URL.
 */
export async function f8Fetch(path: string, init?: RequestInit): Promise<Response> {
  for (let attempt = 0; ; attempt++) {
    const response = await fetch(`${F8}${path}`, init);
    if (response.status === 429) {
      await response.text().catch(() => undefined); // drain so the connection is reusable
      if (attempt >= 20) throw new Error(`${path}: still rate-limited after 20 retries.`);
      const retryAfter = Number(response.headers.get("retry-after"));
      await sleep(Number.isFinite(retryAfter) && retryAfter > 0 ? retryAfter * 1000 : 1500);
      continue;
    }
    return response;
  }
}

/** The product's compile authority. Errors loudly if the apiApp isn't reachable/configured. */
export async function validate(
  kind: DelegateKind,
  fragment: string,
): Promise<ValidationResult> {
  const response = await f8Fetch("/delegates/validate", {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ delegateKind: kind, fragment }),
  });
  if (!response.ok) {
    throw new Error(
      `/delegates/validate returned HTTP ${response.status} - is the apiApp reachable at NL_EVAL_F8 (${F8}) and running without an API key?`,
    );
  }
  return (await response.json()) as ValidationResult;
}

/**
 * The WHOLE-TYPE compile authority (feature plugin-registration): POST
 * /plugins/{category}/validate. Sibling of validate() above - same base URL, same f8Fetch
 * 429-retry, same JSON handling - but it compiles a complete C# type (the body is
 * `{ name, contract?, sourceCode }`, `contract` read only for the algorithm category) and
 * returns `{ valid, error }` rather than a diagnostics list. Unlike /delegates/validate this
 * route is gated by the dynamic-plugin capability + auth, so a 403 is turned into a clear,
 * actionable error rather than an opaque HTTP failure. `category` is "algorithm" | "function".
 */
export async function validatePlugin(
  category: PluginAuthoringCategory,
  spec: PluginValidationSpecification,
): Promise<PluginValidationResult> {
  const response = await f8Fetch(`/plugins/${category}/validate`, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(spec),
  });
  if (response.status === 403) {
    await response.text().catch(() => undefined); // drain so the connection is reusable
    throw new Error(
      `/plugins/${category}/validate returned HTTP 403 - the dynamic-plugin capability is ` +
        `disabled on the F8 box at NL_EVAL_F8 (${F8}). Plugin registration/validation is gated; ` +
        `enable the dynamic-plugin capability on the apiApp and re-run.`,
    );
  }
  if (!response.ok) {
    throw new Error(
      `/plugins/${category}/validate returned HTTP ${response.status} - is the apiApp reachable ` +
        `at NL_EVAL_F8 (${F8}) with the dynamic-plugin capability enabled?`,
    );
  }
  return (await response.json()) as PluginValidationResult;
}

/** Error strings from a validation result (severity=error), formatted "<id> <message>". */
export function compileErrors(result: ValidationResult): string[] {
  return result.diagnostics
    .filter((d) => d.severity === "error")
    .map((d) => `${d.id} ${d.message}`);
}

/**
 * Streaming Ollama chat. The web UI's chatWithModel is non-streaming (fine in a browser);
 * here we stream so headers arrive immediately - a non-streaming call on a slow backend
 * only returns headers once the whole body is ready, tripping Node/undici's 5-minute
 * headers timeout. The final chunk carries the generation stats.
 */
export async function ollamaChat(
  messages: ChatTurn[],
  model: string = MODEL,
): Promise<{ content: string; stats: GenStats | null }> {
  const response = await fetch(`${ENDPOINT}/api/chat`, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ model, messages, stream: true, options: { temperature: 0.1 } }),
    signal: AbortSignal.timeout(PER_CALL_TIMEOUT_MS),
  });
  if (!response.ok || !response.body) {
    throw new Error(`Model endpoint returned HTTP ${response.status}.`);
  }

  let content = "";
  let stats: GenStats | null = null;
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

/** Ollama reachability check (GET /api/version). */
export async function ollamaReachable(): Promise<boolean> {
  try {
    const response = await fetch(`${ENDPOINT}/api/version`);
    return response.ok;
  } catch {
    return false;
  }
}
