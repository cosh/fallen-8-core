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

import { StrictMode, useEffect, useState, type CSSProperties } from "react";
import ReactDOM from "react-dom/client";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { RouterProvider } from "@tanstack/react-router";
import { createStudioRouter, router as standaloneRouter } from "./routes";
import {
  PortalContainerContext,
  StudioConfigContext,
  registerStudioMount,
  themeStyle,
  type StudioConfig,
} from "./studioConfig";
import { applyStudioConfig } from "./applyStudioConfig";
import "../index.css";

/**
 * How to mount Studio: `mountStudio(container, config?)` for an imperative or
 * framework-free host, `<F8Studio config={...}/>` for a React host, and no config at all
 * for the standalone app (main.tsx). The config is read once per mount, so changing it
 * means remounting; the contract itself lives in ./studioConfig.ts.
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
  // Counted so a second SIMULTANEOUS mount fails loudly (see registerStudioMount): it would
  // otherwise silently rebind this tree to the other config's instances and credentials.
  useEffect(registerStudioMount, []);
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
