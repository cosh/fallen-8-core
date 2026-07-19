/**
 * Shared harness plumbing (nl-assist-finetune). One definition of the two external
 * dependencies every script in this pipeline talks to:
 *   - the F8 apiApp's POST /delegates/validate - the product's own compile authority,
 *     used both as the training-set filter (spec FT-2) and the eval metric (FT-4);
 *   - the Ollama chat endpoint - the model transport, streamed so slow generations
 *     don't trip undici's headers timeout.
 * Baseline eval, semantic eval, and the dataset generator all import from here so the
 * authority and the transport can't drift between them (repo rule: one home per thing).
 *
 * Env (shared by every script):
 *   NL_EVAL_MODEL     model name       (default phi4-mini)
 *   NL_EVAL_ENDPOINT  Ollama endpoint  (default http://localhost:11434)
 *   NL_EVAL_F8        apiApp base URL  (default http://localhost:5000; must run with
 *                                       Fallen8__Security__EnableDynamicCodeExecution=true)
 */

import type { DelegateKind } from "../../fallen-8-web-ui/src/api/types";
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
 * The product's compile authority. Errors loudly if the apiApp isn't reachable/configured.
 *
 * /delegates/validate sits behind the sensitive-endpoint fixed-window limiter (default
 * 30 requests / 10 s). A batch caller (the dataset generator) bursts past that, so we
 * transparently retry 429s - honouring Retry-After when the server sends it, otherwise
 * backing off ~1.5 s until the window replenishes. This keeps callers decoupled from the
 * server's limit rather than hard-coding a client-side rate.
 */
export async function validate(
  kind: DelegateKind,
  fragment: string,
): Promise<ValidationResult> {
  const body = JSON.stringify({ delegateKind: kind, fragment });
  for (let attempt = 0; ; attempt++) {
    const response = await fetch(`${F8}/delegates/validate`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body,
    });
    if (response.status === 429) {
      await response.text().catch(() => undefined); // drain so the connection is reusable
      if (attempt >= 20) throw new Error("/delegates/validate: still rate-limited after 20 retries.");
      const retryAfter = Number(response.headers.get("retry-after"));
      await sleep(Number.isFinite(retryAfter) && retryAfter > 0 ? retryAfter * 1000 : 1500);
      continue;
    }
    if (!response.ok) {
      throw new Error(
        `/delegates/validate returned HTTP ${response.status} - is the apiApp running with Fallen8__Security__EnableDynamicCodeExecution=true?`,
      );
    }
    return (await response.json()) as ValidationResult;
  }
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
