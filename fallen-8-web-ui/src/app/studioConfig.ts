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
 * The host-facing embed contract (feature studio-embeddable). Every field is optional and
 * omitting it reproduces the standalone app's behavior exactly; `mountStudio(el)` with no
 * config IS the standalone bootstrap. The full story lives in
 * features/open/studio-embeddable/spec.md - this file is the one home for the contract's
 * runtime side: the current config (set once per mount, before anything renders) and the
 * `storageKey` prefix every persisted key goes through.
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
}

/**
 * The active config. Module state, not React state, ON PURPOSE: the instance registry and
 * the workspace stores read it at store creation/rehydration time, outside any component.
 * It is set exactly once per mount (mountStudio / F8Studio), before the first render.
 */
let current: StudioConfig = {};

export function getStudioConfig(): StudioConfig {
  return current;
}

export function setStudioConfig(config: StudioConfig): void {
  current = config;
}

/**
 * Every persisted localStorage key goes through here (registry, workspace stores, NL-assist,
 * first-run), so a host embed with `storageNamespace` cannot collide with a standalone Studio
 * on the same origin. Default prefix is empty: existing users' f8.* keys are untouched.
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
