import type { NlAssistConfig } from "./config";
import type { NlPrompt } from "./prompt";

/**
 * Browser-to-model transport (nl-assist spec §4): Ollama-native or OpenAI-compatible
 * chat completions. F8 is never in this path; the API key (if any) goes only here
 * (FR-26.11). Each call also surfaces the provider's generation statistics
 * (nl-assist-ux FR-5) — normalized headline numbers plus the raw payload.
 */

export interface ChatTurn {
  role: "system" | "user" | "assistant";
  content: string;
}

export interface NlGenerationStats {
  promptTokens?: number;
  completionTokens?: number;
  durationMs?: number;
  tokensPerSecond?: number;
  /** The provider's stats fields verbatim, for the expandable raw view. */
  raw: Record<string, unknown>;
}

export interface NlChatResult {
  content: string;
  stats: NlGenerationStats | null;
}

interface OllamaChatResponse {
  message?: { content?: string };
  total_duration?: number; // nanoseconds
  load_duration?: number;
  prompt_eval_count?: number;
  prompt_eval_duration?: number;
  eval_count?: number;
  eval_duration?: number;
}

interface OpenAiChatResponse {
  model?: string;
  choices?: { message?: { content?: string } }[];
  usage?: { prompt_tokens?: number; completion_tokens?: number; total_tokens?: number };
}

export async function chatWithModel(
  config: NlAssistConfig,
  messages: ChatTurn[],
  signal?: AbortSignal,
): Promise<NlChatResult> {
  const base = config.endpoint.replace(/\/+$/, "");

  if (config.apiKind === "ollama") {
    const response = await fetch(`${base}/api/chat`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({
        model: config.model,
        messages,
        stream: false,
        options: { temperature: config.temperature },
      }),
      signal,
    });
    if (!response.ok) {
      throw new Error(`Model endpoint returned HTTP ${response.status}.`);
    }
    const data = (await response.json()) as OllamaChatResponse;
    return { content: data.message?.content ?? "", stats: ollamaStats(data) };
  }

  const url = base.endsWith("/v1")
    ? `${base}/chat/completions`
    : `${base}/v1/chat/completions`;
  const headers: Record<string, string> = { "Content-Type": "application/json" };
  if (config.apiKey) headers.Authorization = `Bearer ${config.apiKey}`;

  const response = await fetch(url, {
    method: "POST",
    headers,
    body: JSON.stringify({
      model: config.model,
      messages,
      temperature: config.temperature,
    }),
    signal,
  });
  if (!response.ok) {
    throw new Error(`Model endpoint returned HTTP ${response.status}.`);
  }
  const data = (await response.json()) as OpenAiChatResponse;
  return { content: data.choices?.[0]?.message?.content ?? "", stats: openAiStats(data) };
}

function ollamaStats(data: OllamaChatResponse): NlGenerationStats | null {
  const { message: _message, ...raw } = data;
  if (Object.keys(raw).length === 0) return null;
  return {
    promptTokens: data.prompt_eval_count,
    completionTokens: data.eval_count,
    // Ollama reports durations in nanoseconds.
    durationMs: data.total_duration !== undefined ? data.total_duration / 1e6 : undefined,
    tokensPerSecond:
      data.eval_count !== undefined && data.eval_duration
        ? data.eval_count / (data.eval_duration / 1e9)
        : undefined,
    raw: raw as Record<string, unknown>,
  };
}

function openAiStats(data: OpenAiChatResponse): NlGenerationStats | null {
  if (!data.usage) return null;
  return {
    promptTokens: data.usage.prompt_tokens,
    completionTokens: data.usage.completion_tokens,
    raw: { model: data.model, usage: data.usage },
  };
}

/**
 * Reachability probe (nl-assist-ux FR-2): informational only, never gates generation.
 * Ollama exposes GET /api/version; OpenAI-compatible endpoints expose GET /v1/models.
 */
export async function probeEndpoint(
  config: NlAssistConfig,
  signal?: AbortSignal,
): Promise<boolean> {
  const base = config.endpoint.replace(/\/+$/, "");
  const url =
    config.apiKind === "ollama"
      ? `${base}/api/version`
      : base.endsWith("/v1")
        ? `${base}/models`
        : `${base}/v1/models`;
  const headers: Record<string, string> = {};
  if (config.apiKind === "openai" && config.apiKey) {
    headers.Authorization = `Bearer ${config.apiKey}`;
  }
  try {
    const response = await fetch(url, { headers, signal });
    return response.ok;
  } catch {
    return false;
  }
}

export function initialMessages(prompt: NlPrompt): ChatTurn[] {
  return [
    { role: "system", content: prompt.system },
    { role: "user", content: prompt.user },
  ];
}
