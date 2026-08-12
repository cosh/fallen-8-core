// MIT License
//
// studioConfig.ts
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

import { createContext, useContext } from "react";
import type { QueryClient } from "@tanstack/react-query";
import type { InstanceConfig } from "../instances/types";

/**
 * The host-facing embed contract (feature studio-embeddable) and its runtime state: the
 * active config, the live-mount count, and the `storageKey` prefix every persisted key goes
 * through. Every field is optional and omitting it reproduces the standalone app's behavior.
 * How to mount is documented on ./mount.tsx; the feature's story is in
 * features/done/studio-embeddable/spec.md.
 */

/**
 * The themable design tokens: the `@theme` custom properties in index.css, camel-cased.
 * Overrides land as inline CSS variables on the `.f8-studio` root, so they win over the
 * stylesheet defaults without touching it (Tailwind v4 utilities resolve through var()).
 */
export interface ThemeTokens {
  ink: string;
  panel: string;
  panel2: string;
  line: string;
  fg: string;
  fgDim: string;
  fgFaint: string;
  accent: string;
  accent2: string;
  warn: string;
  danger: string;
  fontMono: string;
}

/** ThemeTokens key -> the CSS custom property it overrides. */
const THEME_VARS: Record<keyof ThemeTokens, string> = {
  ink: "--color-ink",
  panel: "--color-panel",
  panel2: "--color-panel-2",
  line: "--color-line",
  fg: "--color-fg",
  fgDim: "--color-fg-dim",
  fgFaint: "--color-fg-faint",
  accent: "--color-accent",
  accent2: "--color-accent-2",
  warn: "--color-warn",
  danger: "--color-danger",
  fontMono: "--font-mono",
};

export interface StudioConfig {
  /** Host-supplied managed instances (default: the config.js-seeded same-origin instance). */
  instances?: InstanceConfig[];
  activeInstanceId?: string;
  /** Hide the register/edit/remove affordances when the host owns the instance list. */
  lockInstances?: boolean;
  /** Seed the active namespace for the host-supplied instances (default: "default"). */
  namespace?: string;
  /** Hide the namespace switcher and management when the embed is scoped to one graph. */
  lockNamespace?: boolean;
  /** Router basepath (default "": root, as standalone). */
  basepath?: string;
  /** "memory" keeps Studio out of the address bar when the host owns the URL. */
  history?: "browser" | "memory";
  /** Prefix for every persisted localStorage key (default "": today's bare f8.* keys). */
  storageNamespace?: string;
  /** Token overrides; anything omitted keeps today's dark defaults. */
  theme?: Partial<ThemeTokens>;
  /** Reuse the host's QueryClient (default: Studio creates its own, as today). */
  queryClient?: QueryClient;
  /**
   * NL-assist policy for an embed. "disabled" removes the NL panels entirely;
   * "instance-only" locks the model transport to the active instance's POST /chat, so no
   * browser-direct custom endpoint is reachable and no third-party model key is ever held
   * inside the embed. Enforced at the transport choke point (resolveNlConfig in
   * delegate/nl/config.ts, applied by generateChat), not just hidden in the UI: a custom
   * config persisted by an earlier session cannot re-route an embed. Absent: standalone
   * behavior (instance mode default, custom mode available).
   */
  nlAssist?: "disabled" | "instance-only";
}

/**
 * The active config. Module state, not React state, ON PURPOSE: the instance registry and
 * the workspace stores read it at store creation/rehydration time, outside any component.
 * It is set exactly once per mount (mountStudio / F8Studio), before the first render.
 */
let current: StudioConfig = {};

/**
 * How many Studio trees are currently mounted. Two SIMULTANEOUS embeds are an explicit
 * non-goal (see the spec): they would share this module's config, the instance registry
 * singleton and the persisted keys, so the second mount would silently rebind the first to
 * its own instances and credentials. Counting them lets that fail LOUDLY instead.
 */
let liveMounts = 0;

export function getStudioConfig(): StudioConfig {
  return current;
}

export function setStudioConfig(config: StudioConfig): void {
  if (liveMounts > 0 && config !== current) {
    throw new Error(
      "F8 Studio is already mounted. Two simultaneous Studio embeds are not supported " +
        "(they share one instance registry and one set of persisted keys); unmount the " +
        "first, or run the second in its own realm (iframe/worker).",
    );
  }
  current = config;
}

/** Registers a mounted tree; the returned callback releases it on unmount. */
export function registerStudioMount(): () => void {
  liveMounts += 1;
  let released = false;
  return () => {
    if (released) return;
    released = true;
    liveMounts -= 1;
  };
}

/**
 * Every persisted localStorage key goes through here (registry, workspace stores, NL-assist,
 * first-run), so a host embed with `storageNamespace` keeps its state separate from a
 * standalone Studio on the same origin. Default prefix is empty: existing users' f8.* keys
 * are untouched. Separation holds for what is WRITTEN under the prefix; the module-level
 * stores additionally skip their import-time hydration so bare-key state cannot bleed in
 * (see the persist options in instances/registry.ts).
 */
export function storageKey(name: string): string {
  return `${current.storageNamespace ?? ""}${name}`;
}

/** The config.theme overrides as inline style variables for the `.f8-studio` root. */
export function themeStyle(theme: Partial<ThemeTokens> | undefined): Record<string, string> {
  const style: Record<string, string> = {};
  if (!theme) return style;
  for (const [token, value] of Object.entries(theme)) {
    if (value) style[THEME_VARS[token as keyof ThemeTokens]] = value;
  }
  return style;
}

/**
 * Component access to the mount's config. Defaults to the standalone config, so components
 * rendered without a provider (unit tests, storybooks) behave exactly like today's app.
 */
export const StudioConfigContext = createContext<StudioConfig>({});

export function useStudioConfig(): StudioConfig {
  return useContext(StudioConfigContext);
}

/**
 * Where Radix portals render. The mount supplies the `.f8-studio` root so overlays stay
 * inside the scoped, themable subtree (required once the style primitives are scoped to it,
 * and it keeps modals inside a host region instead of escaping to document.body). Without a
 * provider (unit tests rendering a dialog directly) Radix falls back to document.body.
 */
export const PortalContainerContext = createContext<HTMLElement | undefined>(undefined);

export function usePortalContainer(): HTMLElement | undefined {
  return useContext(PortalContainerContext);
}
