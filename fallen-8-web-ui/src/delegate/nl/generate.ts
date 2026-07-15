import type { NlAssistConfig } from "./config";
import type { NlPrompt } from "./prompt";

/**
 * Browser-to-model transport (nl-assist spec §4): Ollama-native or OpenAI-compatible
 * chat completions. F8 is never in this path; the API key (if any) goes only here
 * (FR-26.11).
 */

export interface ChatTurn {
  role: "system" | "user" | "assistant";
  content: string;
}

export async function chatWithModel(
  config: NlAssistConfig,
  messages: ChatTurn[],
  signal?: AbortSignal,
): Promise<string> {
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
    const data = (await response.json()) as { message?: { content?: string } };
    return data.message?.content ?? "";
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
  const data = (await response.json()) as {
    choices?: { message?: { content?: string } }[];
  };
  return data.choices?.[0]?.message?.content ?? "";
}

export function initialMessages(prompt: NlPrompt): ChatTurn[] {
  return [
    { role: "system", content: prompt.system },
    { role: "user", content: prompt.user },
  ];
}
