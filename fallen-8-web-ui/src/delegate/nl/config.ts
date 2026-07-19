import { create } from "zustand";
import { persist } from "zustand/middleware";

/**
 * NL-assist model backend config (nl-assist spec FR-26.4, nl-assist-ux spec §2). GLOBAL
 * scope (not per-instance). The key is stored locally and is only ever sent to the
 * configured model endpoint - never to a Fallen-8 instance (FR-26.11).
 *
 * Two backend modes (nl-assist-ux FR-1/FR-3): "builtin" pins the local Ollama stack from
 * docker-compose.yml and defaults to the fine-tuned "f8-delegate" model (nl-assist-finetune,
 * higher first-pass accuracy on the delegate surface); "custom" uses the stored endpoint
 * fields. To use the stock base model instead, switch to custom and pick the
 * "Ollama (stock phi4-mini)" preset. Always resolve via effectiveNlConfig() before calls.
 */

export type NlBackendMode = "builtin" | "custom";

export interface NlAssistConfig {
  mode: NlBackendMode;
  endpoint: string;
  apiKind: "ollama" | "openai";
  model: string;
  apiKey?: string;
  temperature: number;
  maxRetries: number;
}

/**
 * The local Ollama backend (docker-compose.yml `ollama` service) — not bundled in F8. The
 * default model is the fine-tuned "f8-delegate" (produced by nl-assist-finetune); the
 * compose stack still pulls "phi4-mini" as its base and the selectable fallback. If
 * f8-delegate is not present on the Ollama host, create it with the training pipeline or
 * switch to the stock phi4-mini preset (custom mode) — otherwise calls 404.
 */
export const BUILTIN_NL_BACKEND = {
  endpoint: "http://localhost:11434",
  apiKind: "ollama",
  model: "f8-delegate",
} as const;

export const DEFAULT_NL_CONFIG: NlAssistConfig = {
  mode: "builtin",
  endpoint: "",
  apiKind: "ollama",
  model: "f8-delegate",
  apiKey: undefined,
  temperature: 0.1,
  maxRetries: 2,
};

/** Convenience prefills for custom mode (nl-assist-ux FR-3) — not recommendations. */
export interface NlPreset {
  name: string;
  endpoint: string;
  apiKind: "ollama" | "openai";
  model: string;
}

export const NL_PRESETS: NlPreset[] = [
  { name: "Ollama (stock phi4-mini)", endpoint: "http://localhost:11434", apiKind: "ollama", model: "phi4-mini" },
  { name: "Ollama (fine-tuned f8-delegate)", endpoint: "http://localhost:11434", apiKind: "ollama", model: "f8-delegate" },
  { name: "OpenAI", endpoint: "https://api.openai.com/v1", apiKind: "openai", model: "gpt-4o-mini" },
  { name: "Anthropic", endpoint: "https://api.anthropic.com/v1", apiKind: "openai", model: "claude-opus-4-8" },
];

interface NlAssistState {
  config: NlAssistConfig;
  /** FR-26.10: non-loopback endpoints show the "text leaves this machine" notice once. */
  leaveNoticeAccepted: boolean;
  setConfig: (patch: Partial<NlAssistConfig>) => void;
  acceptLeaveNotice: () => void;
}

/**
 * Persist migration (nl-assist-ux FR-4): pre-mode configs derive it from the stored
 * endpoint — a user who had configured an endpoint keeps it (custom); everyone else
 * lands on the zero-config builtin default.
 */
export function migrateNlState(persisted: unknown): Partial<NlAssistState> {
  const state = (persisted ?? {}) as Partial<NlAssistState>;
  const stored = (state.config ?? {}) as Partial<NlAssistConfig>;
  const mode: NlBackendMode =
    stored.mode ?? ((stored.endpoint ?? "").trim() !== "" ? "custom" : "builtin");
  return { ...state, config: { ...DEFAULT_NL_CONFIG, ...stored, mode } };
}

export const useNlAssist = create<NlAssistState>()(
  persist(
    (set) => ({
      config: DEFAULT_NL_CONFIG,
      leaveNoticeAccepted: false,
      setConfig: (patch) =>
        set((s) => ({
          config: { ...s.config, ...patch },
          // A changed endpoint re-arms the privacy notice.
          leaveNoticeAccepted:
            patch.endpoint !== undefined && patch.endpoint !== s.config.endpoint
              ? false
              : s.leaveNoticeAccepted,
        })),
      acceptLeaveNotice: () => set({ leaveNoticeAccepted: true }),
    }),
    {
      name: "f8.nl-assist",
      version: 1,
      migrate: (persisted) => migrateNlState(persisted) as NlAssistState,
    },
  ),
);

/** The config to actually call with: builtin mode pins the compose-shipped backend. */
export function effectiveNlConfig(config: NlAssistConfig): NlAssistConfig {
  if (config.mode === "builtin") {
    return { ...config, ...BUILTIN_NL_BACKEND, apiKey: undefined };
  }
  return config;
}

/**
 * `enabled` is derived, not stored: builtin is always configured (nl-assist-ux FR-1);
 * custom needs an endpoint and model (FR-26.8).
 */
export function isNlConfigured(config: NlAssistConfig): boolean {
  if (config.mode === "builtin") return true;
  return config.endpoint.trim() !== "" && config.model.trim() !== "";
}

/**
 * FR-26.12: the native Ollama path (e.g. the builtin local phi4-mini setup) never
 * authenticates - the Ollama transport sends no Authorization header at all. Only
 * OpenAI-compatible custom endpoints can carry a key, so only they get the field.
 */
export function usesApiKey(config: NlAssistConfig): boolean {
  return config.apiKind === "openai";
}

/** FR-26.10: loopback endpoints never show the privacy notice - nothing leaves. */
export function isLoopbackEndpoint(endpoint: string): boolean {
  try {
    const hostname = new URL(endpoint).hostname.toLowerCase();
    return (
      hostname === "localhost" ||
      hostname === "127.0.0.1" ||
      hostname === "[::1]" ||
      hostname === "::1"
    );
  } catch {
    return false;
  }
}
