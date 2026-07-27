// MIT License
//
// generate.ts
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

import { effectiveNlConfig, type NlAssistConfig } from "./config";
import type { NlPrompt } from "./prompt";
import { postChat } from "../../api/endpoints";
import type { InstanceConfig } from "../../instances/types";

/**
 * Model transport (nl-assist spec §4, feature instance-config). Two paths, chosen by mode:
 * - INSTANCE (default): browser -> the active Fallen-8 instance (POST /chat) -> its model
 *   backend. Auth is the instance's own credential (the shared api client); no third-party
 *   key is involved.
 * - CUSTOM: browser DIRECTLY to an Ollama-native or OpenAI-compatible endpoint; F8 is never
 *   in this path and the API key (if any) goes only here (FR-26.11).
 * Either path surfaces the provider's generation statistics (nl-assist-ux FR-5).
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

/**
 * The single entry point both NL panels call. Routes to the instance gateway or a
 * browser-direct custom endpoint per {@link NlAssistConfig.mode}.
 */
export async function generateChat(
  config: NlAssistConfig,
  instance: InstanceConfig | null,
  messages: ChatTurn[],
  signal?: AbortSignal,
): Promise<NlChatResult> {
  if (config.mode === "instance") {
    if (!instance) {
      throw new Error("No active instance to route the model call through.");
    }
    return chatViaInstance(instance, messages, config.temperature, signal);
  }
  return chatWithModel(effectiveNlConfig(config), messages, signal);
}

/** Instance-gateway path: browser -> F8 POST /chat -> the server's model backend. */
export async function chatViaInstance(
  instance: InstanceConfig,
  messages: ChatTurn[],
  temperature: number,
  signal?: AbortSignal,
): Promise<NlChatResult> {
  const result = await postChat(
    instance,
    { messages: messages.map((m) => ({ role: m.role, content: m.content })), options: { temperature } },
    signal,
  );
  if (!result) {
    throw new Error("The instance returned no chat completion.");
  }
  const s = result.stats;
  const stats: NlGenerationStats | null = s
    ? {
        promptTokens: s.promptTokens ?? undefined,
        completionTokens: s.completionTokens ?? undefined,
        durationMs: s.durationMs ?? undefined,
        tokensPerSecond: s.tokensPerSecond ?? undefined,
        raw: { model: result.model, ...s },
      }
    : null;
  return { content: result.content, stats };
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
