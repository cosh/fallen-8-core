// MIT License
//
// mount.tsx
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

import { StrictMode, useState, type CSSProperties } from "react";
import ReactDOM from "react-dom/client";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { RouterProvider } from "@tanstack/react-router";
import { createStudioRouter, router as standaloneRouter } from "./routes";
import {
  PortalContainerContext,
  StudioConfigContext,
  setStudioConfig,
  storageKey,
  themeStyle,
  type StudioConfig,
} from "./studioConfig";
import { applyStudioConfigToRegistry } from "../instances/registry";
import { useNlAssist } from "../delegate/nl/config";
import { useFirstRun } from "../firstrun/firstRunStore";
import "../index.css";

/**
 * The mount seam (feature studio-embeddable): `mountStudio(el)` with no config IS the
 * standalone bootstrap main.tsx used to inline, and every StudioConfig field defaults to
 * that behavior. A host portal embeds Studio either as `mountStudio(container, config)`
 * (imperative, framework-free) or as `<F8Studio config={...}/>` (a React host). The config
 * is read once per mount; changing it means remounting.
 */

/** Studio's own QueryClient - the exact defaults the standalone app has always used. */
export function createStudioQueryClient(): QueryClient {
  return new QueryClient({
    defaultOptions: {
      queries: {
        retry: 1,
        refetchOnWindowFocus: false,
        staleTime: 5_000,
      },
    },
  });
}

/**
 * Make a mount's config the active one BEFORE anything renders: the persisted stores
 * (registry, NL-assist, first-run; the per-instance workspace stores are created lazily and
 * pick the prefix up on creation) are re-pointed at the possibly prefixed storage keys and
 * re-hydrated, which also injects the host's managed instances and namespace pin via the
 * registry's merge. localStorage is synchronous, so the state is in place when the first
 * render reads it. Idempotent on purpose: StrictMode double-invokes initializers.
 */
function applyStudioConfig(config: StudioConfig): void {
  setStudioConfig(config);
  void applyStudioConfigToRegistry();
  useNlAssist.persist.setOptions({ name: storageKey("f8.nl-assist") });
  void useNlAssist.persist.rehydrate();
  useFirstRun.persist.setOptions({ name: storageKey("f8.first-run") });
  void useFirstRun.persist.rehydrate();
}

function pickRouter(config: StudioConfig) {
  // The standalone router is shared module state (and the Register type anchor); a mount
  // only builds its own when a host actually customized routing.
  return config.basepath || config.history === "memory"
    ? createStudioRouter(config)
    : standaloneRouter;
}

/**
 * The provider tree main.tsx used to own, wrapped in the `.f8-studio` scope root: the style
 * primitives are scoped under it, theme token overrides land on it as inline CSS variables,
 * and Radix portals target it so overlays stay inside the (possibly embedded) subtree.
 */
function StudioTree({
  config,
  router,
  queryClient,
}: {
  config: StudioConfig;
  router: ReturnType<typeof createStudioRouter>;
  queryClient: QueryClient;
}) {
  const [portalEl, setPortalEl] = useState<HTMLElement | undefined>(undefined);
  return (
    <StudioConfigContext.Provider value={config}>
      <div
        ref={(el) => setPortalEl(el ?? undefined)}
        className="f8-studio"
        data-testid="f8-studio-root"
        style={themeStyle(config.theme) as CSSProperties}
      >
        <PortalContainerContext.Provider value={portalEl}>
          <QueryClientProvider client={queryClient}>
            <RouterProvider router={router} />
          </QueryClientProvider>
        </PortalContainerContext.Provider>
      </div>
    </StudioConfigContext.Provider>
  );
}

/** React-host embedding. No StrictMode wrapper - that is the host's call. */
export function F8Studio({ config = {} }: { config?: StudioConfig }) {
  const [setup] = useState(() => {
    applyStudioConfig(config);
    return {
      config,
      router: pickRouter(config),
      queryClient: config.queryClient ?? createStudioQueryClient(),
    };
  });
  return <StudioTree config={setup.config} router={setup.router} queryClient={setup.queryClient} />;
}

/** Imperative mounting - the standalone entry (main.tsx) and framework-free hosts. */
export function mountStudio(
  el: HTMLElement,
  config: StudioConfig = {},
): { unmount(): void } {
  const root = ReactDOM.createRoot(el);
  root.render(
    <StrictMode>
      <F8Studio config={config} />
    </StrictMode>,
  );
  return { unmount: () => root.unmount() };
}
