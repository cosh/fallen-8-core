// MIT License
//
// config.ts
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

import { createElement, type ComponentType } from "react";
import { create } from "zustand";
import { persist } from "zustand/middleware";
import { getStudioConfig, storageKey, useStudioConfig } from "../../app/studioConfig";

/**
 * NL-assist model backend config (nl-assist spec FR-26.4, nl-assist-ux spec §2, feature
 * instance-config). GLOBAL scope (not per-instance): it is a single browser preference for
 * WHERE model calls go, and instance mode targets whichever instance is active.
 *
 * Two modes:
 * - "instance" (default): the call is proxied THROUGH the active Fallen-8 instance
 *   (browser -> POST /chat -> the instance's model backend, e.g. the Ollama sidecar). The
 *   model is server-owned (Fallen8:Chat); nothing is configured here, and the prompt travels
 *   to the same instance the user already trusts with their graph, so there is no egress
 *   notice. This replaces the old browser-direct "builtin" mode (feature instance-config
 *   retired the "never through the instance" default; see docs/studio.md).
 * - "custom": the browser calls a model endpoint DIRECTLY (Ollama-native or OpenAI-compatible),
 *   with any API key held only in the browser and never sent to a Fallen-8 instance (FR-26.11).
 *   A non-loopback custom endpoint shows the "text leaves this machine" notice.
 */

export type NlBackendMode = "instance" | "custom";

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
 * The default model the compose stack pulls for the instance's chat backend (produced by
 * nl-assist-finetune). Shown as a hint only; in instance mode the server owns the model, and
 * in custom mode the user picks one (see NL_PRESETS). If the chosen model is absent on the
 * backend, calls 404 - create it with the training pipeline or pick the stock phi4-mini preset.
 */
export const DEFAULT_NL_MODEL = "phi4-f8-mini";

export const DEFAULT_NL_CONFIG: NlAssistConfig = {
  mode: "instance",
  endpoint: "",
  apiKind: "ollama",
  model: DEFAULT_NL_MODEL,
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
  { name: "Ollama (fine-tuned phi4-f8-mini)", endpoint: "http://localhost:11434", apiKind: "ollama", model: "phi4-f8-mini" },
  { name: "Ollama (fine-tuned phi4-f8 — GPU)", endpoint: "http://localhost:11434", apiKind: "ollama", model: "phi4-f8" },
  { name: "Ollama (stock phi4-mini)", endpoint: "http://localhost:11434", apiKind: "ollama", model: "phi4-mini" },
  { name: "Ollama (stock phi4 — GPU)", endpoint: "http://localhost:11434", apiKind: "ollama", model: "phi4" },
  { name: "OpenAI", endpoint: "https://api.openai.com/v1", apiKind: "openai", model: "gpt-4o-mini" },
  { name: "Anthropic", endpoint: "https://api.anthropic.com/v1", apiKind: "openai", model: "claude-opus-4-8" },
];

interface NlAssistState {
  config: NlAssistConfig;
  /** FR-26.10: non-loopback CUSTOM endpoints show the "text leaves this machine" notice once. */
  leaveNoticeAccepted: boolean;
  setConfig: (patch: Partial<NlAssistConfig>) => void;
  acceptLeaveNotice: () => void;
}

/**
 * Persist migration. The legacy browser-direct "builtin" mode becomes "instance" (feature
 * instance-config): those users now route through the active instance rather than a fixed
 * localhost Ollama. A pre-mode config with a stored endpoint stays "custom"; everyone else
 * lands on the instance default.
 */
export function migrateNlState(persisted: unknown): Partial<NlAssistState> {
  const state = (persisted ?? {}) as Partial<NlAssistState>;
  const stored = (state.config ?? {}) as Partial<NlAssistConfig>;
  // Read the persisted mode as a raw string: it may be the legacy "builtin", which is no
  // longer a member of NlBackendMode.
  const raw = stored.mode as string | undefined;
  const mode: NlBackendMode =
    raw === "custom"
      ? "custom"
      : raw === "builtin" || raw === "instance"
        ? "instance"
        : (stored.endpoint ?? "").trim() !== ""
          ? "custom"
          : "instance";
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
      name: storageKey("f8.nl-assist"),
      // Created at module import, before any StudioConfig exists: hydrating past the bare
      // key here would let a prefixed embed inherit (and then persist under the tenant
      // prefix) the standalone user's config, apiKey included. Every mount path calls
      // applyStudioConfig -> rehydrate against the resolved key. See app/studioConfig.ts.
      skipHydration: true,
      version: 2,
      migrate: (persisted) => migrateNlState(persisted) as NlAssistState,
      // Derived from STORAGE ONLY, never from `current`: hydrating against an empty key (a
      // mount that switched storageNamespace) would otherwise keep the previous mount's
      // config - the browser-held LLM apiKey included - and persist it into the new tenant's
      // universe on the next write. migrateNlState defaults every field, so an absent or
      // partial blob lands on DEFAULT_NL_CONFIG. The embed policy is applied HERE, at the
      // same altitude the registry applies its embed policies: under "instance-only" the
      // store never holds a custom-mode config or a third-party key, so a settings write
      // from inside the embed re-persists the policy-clean shape (and, when the embed
      // shares an unprefixed storage with the standalone app, drops the stored key rather
      // than carrying it).
      merge: (persisted, current) => {
        const migrated = migrateNlState(persisted);
        return {
          ...current,
          ...migrated,
          config: resolveNlConfig(migrated.config ?? DEFAULT_NL_CONFIG),
          leaveNoticeAccepted:
            (persisted as Partial<NlAssistState> | undefined)?.leaveNoticeAccepted ?? false,
        };
      },
    },
  ),
);

/**
 * The embed-policy resolution (StudioConfig.nlAssist, documented on app/studioConfig.ts):
 * under "instance-only" a custom config is forced back to instance mode with its key
 * cleared. Applied at THREE altitudes so the policy holds structurally: the persist merge
 * above (the store never holds a violating config), the transport (generateChat, plus the
 * browser-direct functions refusing outright), and as a belt inside NlBackendConfig's
 * locked rendering.
 */
export function resolveNlConfig(config: NlAssistConfig): NlAssistConfig {
  if (getStudioConfig().nlAssist === "instance-only" && config.mode !== "instance") {
    return { ...config, mode: "instance", apiKey: undefined };
  }
  return config;
}

/**
 * The one panel gate (StudioConfig.nlAssist === "disabled"): both NL panels wrap their
 * inner component with this, so the affordance disappears identically everywhere and the
 * early return cannot trip the rules of hooks inside the inner component. Presentation
 * only - the transport refuses independently.
 */
export function withNlAssistPolicyGate<P extends object>(Inner: ComponentType<P>) {
  return function GatedNlAssistPanel(props: P) {
    if (useStudioConfig().nlAssist === "disabled") return null;
    return createElement(Inner, props);
  };
}

/**
 * The config to actually call a CUSTOM endpoint with. Instance mode does not go through this
 * path (the transport routes to the active instance's /chat); it returns the config unchanged
 * with any stray key cleared, so a status/probe read is harmless.
 */
export function effectiveNlConfig(config: NlAssistConfig): NlAssistConfig {
  if (config.mode === "instance") {
    return { ...config, apiKey: undefined };
  }
  return config;
}

/**
 * `enabled` is derived, not stored: instance mode is configured as long as an instance is
 * active (checked by the caller); custom needs an endpoint and model (FR-26.8).
 */
export function isNlConfigured(config: NlAssistConfig): boolean {
  if (config.mode === "instance") return true;
  return config.endpoint.trim() !== "" && config.model.trim() !== "";
}

/**
 * FR-26.12: the native Ollama path never authenticates - the Ollama transport sends no
 * Authorization header. Only OpenAI-compatible custom endpoints can carry a key.
 */
export function usesApiKey(config: NlAssistConfig): boolean {
  return config.mode === "custom" && config.apiKind === "openai";
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
