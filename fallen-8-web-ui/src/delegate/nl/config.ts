import { create } from "zustand";
import { persist } from "zustand/middleware";

/**
 * NL-assist model backend config (nl-assist spec FR-26.4). GLOBAL scope (not
 * per-instance). The key is stored locally and is only ever sent to the configured model
 * endpoint - never to a Fallen-8 instance (FR-26.11).
 */

export interface NlAssistConfig {
  endpoint: string;
  apiKind: "ollama" | "openai";
  model: string;
  apiKey?: string;
  temperature: number;
  maxRetries: number;
}

export const DEFAULT_NL_CONFIG: NlAssistConfig = {
  endpoint: "",
  apiKind: "ollama",
  model: "phi4-mini",
  apiKey: undefined,
  temperature: 0.1,
  maxRetries: 2,
};

interface NlAssistState {
  config: NlAssistConfig;
  /** FR-26.10: non-loopback endpoints show the "text leaves this machine" notice once. */
  leaveNoticeAccepted: boolean;
  setConfig: (patch: Partial<NlAssistConfig>) => void;
  acceptLeaveNotice: () => void;
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
    { name: "f8.nl-assist" },
  ),
);

/** `enabled` is derived, not stored: no endpoint means no assist (FR-26.8). */
export function isNlConfigured(config: NlAssistConfig): boolean {
  return config.endpoint.trim() !== "" && config.model.trim() !== "";
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
